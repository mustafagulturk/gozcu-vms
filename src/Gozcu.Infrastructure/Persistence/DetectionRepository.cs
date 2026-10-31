using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Gozcu.Contracts.Analysis;
using Npgsql;

namespace Gozcu.Infrastructure.Persistence;

/// <summary>Analiz servisinin tespit kareleri ve olayları.</summary>
public sealed class DetectionRepository
{
    private const string FrameColumns = "id, camera_id AS CameraId, captured_at AS CapturedAt, inference_ms AS InferenceMs, detections::text AS Detections";

    private const string EventColumns =
        "id, camera_id AS CameraId, category, started_at AS StartedAt, ended_at AS EndedAt, max_count AS MaxCount, " +
        "peak_confidence AS PeakConfidence, frame_count AS FrameCount";

    /// <summary>jsonb sütunundaki tespitlerin biçimi: küçük harfli alan adları, kategori metin olarak.</summary>
    internal static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly NpgsqlDataSource _dataSource;

    public DetectionRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>Kareleri tek komutta ekler (unnest ile çok satırlı INSERT).</summary>
    public async Task InsertFramesAsync(IReadOnlyCollection<DetectionFrameRecord> frames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
        {
            return;
        }

        var cameraIds = frames.Select(f => f.CameraId).ToArray();
        var capturedAts = frames.Select(f => f.CapturedAt.ToUniversalTime()).ToArray();
        var inferenceMs = frames.Select(f => f.InferenceMs).ToArray();
        var detections = frames.Select(f => JsonSerializer.Serialize(f.Detections, JsonOptions)).ToArray();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO detection_frames (camera_id, captured_at, inference_ms, detections)
            SELECT c, t, i, d::jsonb
              FROM unnest(@cameraIds::text[], @capturedAts::timestamptz[], @inferenceMs::real[], @detections::text[]) AS x(c, t, i, d)
            """,
            new { cameraIds, capturedAts, inferenceMs, detections },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Olayları ekler ya da günceller (açılış, ara güncelleme, kapanış aynı çağrı). Kapanmış bir olayın bitişi
    /// sonradan gelen (bitişsiz) bir güncellemeyle silinmez.
    /// </summary>
    public async Task UpsertEventsAsync(IReadOnlyCollection<DetectionEventRecord> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var e in events)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO detection_events (id, camera_id, category, started_at, ended_at, max_count, peak_confidence, frame_count)
                VALUES (@id, @cameraId, @category, @startedAt, @endedAt, @maxCount, @peakConfidence, @frameCount)
                ON CONFLICT (id) DO UPDATE
                   SET ended_at = COALESCE(EXCLUDED.ended_at, detection_events.ended_at),
                       max_count = GREATEST(EXCLUDED.max_count, detection_events.max_count),
                       peak_confidence = GREATEST(EXCLUDED.peak_confidence, detection_events.peak_confidence),
                       frame_count = GREATEST(EXCLUDED.frame_count, detection_events.frame_count)
                """,
                new
                {
                    id = e.Id,
                    cameraId = e.CameraId,
                    category = e.Category.ToString(),
                    startedAt = e.StartedAt.ToUniversalTime(),
                    endedAt = e.EndedAt?.ToUniversalTime(),
                    maxCount = e.MaxCount,
                    peakConfidence = e.PeakConfidence,
                    frameCount = e.FrameCount,
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Çökme kurtarması: servis açılırken hâlâ açık görünen olayları kapatır. Bitiş, o kameranın olay başladıktan
    /// sonra aynı kategoriyi içeren son karesinin zamanıdır; böyle kare yoksa başlangıç zamanı.
    /// </summary>
    /// <returns>Kapatılan olay sayısı.</returns>
    public async Task<int> CloseDanglingEventsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE detection_events e
               SET ended_at = GREATEST(e.started_at, COALESCE(
                       (SELECT max(f.captured_at)
                          FROM detection_frames f
                         WHERE f.camera_id = e.camera_id
                           AND f.captured_at >= e.started_at
                           AND f.detections @> jsonb_build_array(jsonb_build_object('category', e.category))),
                       e.started_at))
             WHERE e.ended_at IS NULL
            """,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Saklama süresi dolan kareleri siler.</summary>
    /// <returns>Silinen satır sayısı.</returns>
    public async Task<int> DeleteFramesOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM detection_frames WHERE captured_at < @cutoff",
            new { cutoff = cutoff.ToUniversalTime() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Bir kameranın verilen aralıktaki tespit karelerini yeniden eskiye döner.</summary>
    public async Task<IReadOnlyList<DetectionFrame>> FindFramesAsync(
        string cameraId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<FrameRow>(new CommandDefinition(
            $"""
            SELECT {FrameColumns}
              FROM detection_frames
             WHERE camera_id = @cameraId
               AND (@from::timestamptz IS NULL OR captured_at >= @from)
               AND (@to::timestamptz IS NULL OR captured_at < @to)
             ORDER BY captured_at DESC
             LIMIT @limit
            """,
            new { cameraId, from = from?.ToUniversalTime(), to = to?.ToUniversalTime(), limit = Math.Clamp(limit, 1, 1000) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(r => r.ToFrame()).ToList();
    }

    /// <summary>Verilen aralıkla kesişen olayları yeniden eskiye döner. Süren olaylar "şimdiye kadar" sürmüş sayılır.</summary>
    public async Task<IReadOnlyList<DetectionEventRecord>> FindEventsAsync(
        string? cameraId,
        ObjectCategory? category,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<EventRow>(new CommandDefinition(
            $"""
            SELECT {EventColumns}
              FROM detection_events
             WHERE (@cameraId::text IS NULL OR camera_id = @cameraId)
               AND (@category::text IS NULL OR category = @category)
               AND (@from::timestamptz IS NULL OR ended_at IS NULL OR ended_at >= @from)
               AND (@to::timestamptz IS NULL OR started_at < @to)
             ORDER BY started_at DESC
             LIMIT @limit
            """,
            new
            {
                cameraId,
                category = category?.ToString(),
                from = from?.ToUniversalTime(),
                to = to?.ToUniversalTime(),
                limit = Math.Clamp(limit, 1, 1000),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(r => r.ToEvent()).ToList();
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    // Npgsql timestamptz değerlerini UTC DateTime olarak okur; kayda dönüştürülürken DateTimeOffset yapılır.
    private sealed class FrameRow
    {
        public long Id { get; init; }

        public string CameraId { get; init; } = string.Empty;

        public DateTime CapturedAt { get; init; }

        public float InferenceMs { get; init; }

        public string Detections { get; init; } = "[]";

        public DetectionFrame ToFrame() => new(
            Id,
            CameraId,
            Utc(CapturedAt),
            InferenceMs,
            JsonSerializer.Deserialize<List<StoredDetection>>(Detections, JsonOptions) ?? []);
    }

    private sealed class EventRow
    {
        public Guid Id { get; init; }

        public string CameraId { get; init; } = string.Empty;

        public string Category { get; init; } = string.Empty;

        public DateTime StartedAt { get; init; }

        public DateTime? EndedAt { get; init; }

        public int MaxCount { get; init; }

        public float PeakConfidence { get; init; }

        public int FrameCount { get; init; }

        public DetectionEventRecord ToEvent() => new(
            Id,
            CameraId,
            Enum.Parse<ObjectCategory>(Category),
            Utc(StartedAt),
            EndedAt is null ? null : Utc(EndedAt.Value),
            MaxCount,
            PeakConfidence,
            FrameCount);
    }
}
