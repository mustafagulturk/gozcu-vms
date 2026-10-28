using Dapper;
using Npgsql;

namespace Gozcu.Infrastructure.Persistence;

public sealed class SegmentRepository
{
    private const string Columns = "id, camera_id AS CameraId, file_path AS FilePath, started_at AS StartedAt, ended_at AS EndedAt, size_bytes AS SizeBytes";

    private readonly NpgsqlDataSource _dataSource;

    public SegmentRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>Parçayı ekler; aynı dosya daha önce eklendiyse günceller. Kaydın kimliğini döner.</summary>
    public async Task<long> UpsertAsync(
        string cameraId,
        string filePath,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        long sizeBytes,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO recording_segments (camera_id, file_path, started_at, ended_at, size_bytes)
            VALUES (@cameraId, @filePath, @startedAt, @endedAt, @sizeBytes)
            ON CONFLICT (file_path) DO UPDATE
               SET ended_at = EXCLUDED.ended_at,
                   size_bytes = EXCLUDED.size_bytes,
                   deleted_at = NULL
            RETURNING id
            """,
            new { cameraId, filePath, startedAt = startedAt.ToUniversalTime(), endedAt = endedAt.ToUniversalTime(), sizeBytes },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task MarkDeletedAsync(IEnumerable<string> filePaths, DateTimeOffset deletedAt, CancellationToken cancellationToken)
    {
        var paths = filePaths.ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE recording_segments SET deleted_at = @deletedAt WHERE file_path = ANY(@paths) AND deleted_at IS NULL",
            new { paths, deletedAt = deletedAt.ToUniversalTime() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Bir kameranın verilen zaman aralığıyla kesişen, silinmemiş parçalarını eskiden yeniye döner.</summary>
    public async Task<IReadOnlyList<RecordingSegment>> FindAsync(
        string? cameraId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<SegmentRow>(new CommandDefinition(
            $"""
            SELECT {Columns}
              FROM recording_segments
             WHERE deleted_at IS NULL
               AND (@cameraId::text IS NULL OR camera_id = @cameraId)
               AND (@from::timestamptz IS NULL OR ended_at > @from)
               AND (@to::timestamptz IS NULL OR started_at < @to)
             ORDER BY started_at
             LIMIT @limit
            """,
            new { cameraId, from = from?.ToUniversalTime(), to = to?.ToUniversalTime(), limit = Math.Clamp(limit, 1, 1000) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(r => r.ToSegment()).ToList();
    }

    // Npgsql timestamptz değerlerini UTC DateTime olarak okur; kayda dönüştürülürken DateTimeOffset yapılır.
    private sealed class SegmentRow
    {
        public long Id { get; init; }

        public string CameraId { get; init; } = string.Empty;

        public string FilePath { get; init; } = string.Empty;

        public DateTime StartedAt { get; init; }

        public DateTime EndedAt { get; init; }

        public long SizeBytes { get; init; }

        public RecordingSegment ToSegment() => new(
            Id, CameraId, FilePath,
            new DateTimeOffset(DateTime.SpecifyKind(StartedAt, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(EndedAt, DateTimeKind.Utc)),
            SizeBytes);
    }
}
