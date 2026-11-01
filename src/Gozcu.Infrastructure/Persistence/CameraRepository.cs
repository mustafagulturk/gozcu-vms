using Dapper;
using Gozcu.Contracts.Cameras;
using Npgsql;

namespace Gozcu.Infrastructure.Persistence;

/// <summary>Veritabanındaki bir kamera satırı. <see cref="Version"/> iyimser eşzamanlılık (ETag) için kullanılır.</summary>
public sealed record CameraRecord(
    string Id,
    string Name,
    string RtspUrl,
    bool Enabled,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Servislerin kullandığı tanıma dönüştürür. Adres ayrıştırılamıyorsa <c>RtspUrl</c> null olur.</summary>
    public CameraDefinition ToDefinition() => new()
    {
        Id = Id,
        Name = Name,
        RtspUrl = Uri.TryCreate(RtspUrl, UriKind.Absolute, out var url) ? url : null,
        Enabled = Enabled,
    };
}

public enum CameraWriteStatus
{
    Succeeded,
    NotFound,

    /// <summary>İstenen sürüm (If-Match) veritabanındakiyle uyuşmuyor.</summary>
    VersionMismatch,

    /// <summary>Aynı kimlikte (büyük/küçük harf duyarsız) bir kamera zaten var.</summary>
    Conflict,
}

/// <summary>Bir yazma işleminin sonucu. Başarılıysa ya da sürüm uyuşmuyorsa <see cref="Camera"/> güncel satırdır.</summary>
public sealed record CameraWriteResult(CameraWriteStatus Status, CameraRecord? Camera)
{
    public bool Succeeded => Status == CameraWriteStatus.Succeeded;
}

/// <summary>Kamera kataloğunun veritabanı erişimi. Tek doğruluk kaynağı <c>cameras</c> tablosudur.</summary>
public sealed class CameraRepository
{
    // "gozcu-cameras" anlamında sabit advisory lock anahtarı (şema betiklerininkinden farklı).
    private const long SeedLockKey = 0x676F7A6363616D;

    private const string Columns =
        "id AS Id, name AS Name, rtsp_url AS RtspUrl, enabled AS Enabled, version AS Version, created_at AS CreatedAt, updated_at AS UpdatedAt";

    private readonly NpgsqlDataSource _dataSource;

    public CameraRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>Tüm kameralar (devre dışı olanlar dahil), kimliğe göre sıralı.</summary>
    public async Task<IReadOnlyList<CameraRecord>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<CameraRow>(new CommandDefinition(
            $"SELECT {Columns} FROM cameras ORDER BY id COLLATE \"C\"", cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(r => r.ToRecord()).ToList();
    }

    public async Task<CameraRecord?> FindAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await FindAsync(connection, id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Yeni kamera ekler. Aynı kimlik büyük/küçük harf farkıyla bile varsa <see cref="CameraWriteStatus.Conflict"/> döner:
    /// servislerin kayıt dizinleri ve bellek içi tabloları kimliği harf duyarsız karşılaştırır.
    /// </summary>
    public async Task<CameraWriteResult> InsertAsync(CameraDefinition camera, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(camera);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<CameraRow>(new CommandDefinition(
            $"""
            INSERT INTO cameras (id, name, rtsp_url, enabled)
            SELECT @Id, @Name, @RtspUrl, @Enabled
             WHERE NOT EXISTS (SELECT 1 FROM cameras WHERE lower(id) = lower(@Id))
            ON CONFLICT (id) DO NOTHING
            RETURNING {Columns}
            """,
            Parameters(camera),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null
            ? new CameraWriteResult(CameraWriteStatus.Conflict, null)
            : new CameraWriteResult(CameraWriteStatus.Succeeded, row.ToRecord());
    }

    /// <summary>
    /// Kamerayı günceller ve sürümünü bir artırır. <paramref name="rtspUrl"/> ya da <paramref name="enabled"/> null ise
    /// mevcut değer korunur.
    /// <paramref name="expectedVersion"/> verilmişse yalnızca o sürümdeki satır güncellenir.
    /// </summary>
    public async Task<CameraWriteResult> UpdateAsync(
        string id,
        string name,
        Uri? rtspUrl,
        bool? enabled,
        int? expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<CameraRow>(new CommandDefinition(
            $"""
            UPDATE cameras
               SET name = @name,
                   rtsp_url = COALESCE(@rtspUrl::text, rtsp_url),
                   enabled = COALESCE(@enabled::boolean, enabled),
                   version = version + 1,
                   updated_at = now()
             WHERE id = @id
               AND (@expectedVersion::integer IS NULL OR version = @expectedVersion)
            RETURNING {Columns}
            """,
            new { id, name, rtspUrl = rtspUrl?.OriginalString, enabled, expectedVersion },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is not null
            ? new CameraWriteResult(CameraWriteStatus.Succeeded, row.ToRecord())
            : await ExplainFailureAsync(connection, id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Kamerayı siler. Kayıt parçaları ve tespitler silinmez; saklama politikası zamanla temizler.</summary>
    public async Task<CameraWriteResult> DeleteAsync(string id, int? expectedVersion, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<CameraRow>(new CommandDefinition(
            $"""
            DELETE FROM cameras
             WHERE id = @id
               AND (@expectedVersion::integer IS NULL OR version = @expectedVersion)
            RETURNING {Columns}
            """,
            new { id, expectedVersion },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is not null
            ? new CameraWriteResult(CameraWriteStatus.Succeeded, row.ToRecord())
            : await ExplainFailureAsync(connection, id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tablo boşsa verilen kameraları yazar ve yazılan sayıyı döner; doluysa hiçbir şey yapmaz ve 0 döner.
    /// Birden fazla örnek aynı anda açılsa da advisory lock sayesinde tohumlama yalnızca bir kez yapılır.
    /// </summary>
    public async Task<int> SeedIfEmptyAsync(IEnumerable<CameraDefinition> cameras, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cameras);
        var items = cameras.ToList();
        if (items.Count == 0)
        {
            return 0;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(@key)", new { key = SeedLockKey }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        var any = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM cameras)", transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (any)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        foreach (var camera in items)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO cameras (id, name, rtsp_url, enabled) VALUES (@Id, @Name, @RtspUrl, @Enabled)",
                Parameters(camera),
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return items.Count;
    }

    private static object Parameters(CameraDefinition camera) => new
    {
        camera.Id,
        Name = string.IsNullOrWhiteSpace(camera.Name) ? camera.Id : camera.Name.Trim(),
        RtspUrl = camera.RtspUrl?.OriginalString ?? string.Empty,
        camera.Enabled,
    };

    private static async Task<CameraWriteResult> ExplainFailureAsync(NpgsqlConnection connection, string id, CancellationToken cancellationToken)
    {
        var current = await FindAsync(connection, id, cancellationToken).ConfigureAwait(false);
        return current is null
            ? new CameraWriteResult(CameraWriteStatus.NotFound, null)
            : new CameraWriteResult(CameraWriteStatus.VersionMismatch, current);
    }

    private static async Task<CameraRecord?> FindAsync(NpgsqlConnection connection, string id, CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<CameraRow>(new CommandDefinition(
            $"SELECT {Columns} FROM cameras WHERE id = @id", new { id }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row?.ToRecord();
    }

    // Npgsql timestamptz değerlerini UTC DateTime olarak okur; kayda dönüştürülürken DateTimeOffset yapılır.
    private sealed class CameraRow
    {
        public string Id { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string RtspUrl { get; init; } = string.Empty;

        public bool Enabled { get; init; }

        public int Version { get; init; }

        public DateTime CreatedAt { get; init; }

        public DateTime UpdatedAt { get; init; }

        public CameraRecord ToRecord() => new(
            Id, Name, RtspUrl, Enabled, Version,
            new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)));
    }
}
