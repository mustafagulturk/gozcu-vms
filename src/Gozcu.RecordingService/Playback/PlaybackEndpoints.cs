using System.Globalization;
using Gozcu.Contracts.Cameras;
using Gozcu.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Gozcu.RecordingService.Playback;

/// <summary>
/// Saate göre geri oynatma API'si: zaman çizelgesi, konum bulma, parça dosyası (HTTP Range destekli) ve klip dışa aktarma.
/// </summary>
public static class PlaybackEndpoints
{
    /// <summary>Zaman çizelgesinde istenebilecek en uzun aralık.</summary>
    public static readonly TimeSpan MaxTimelineRange = TimeSpan.FromHours(24);

    /// <summary>Varsayılan zaman çizelgesi aralığı (son bir saat).</summary>
    public static readonly TimeSpan DefaultTimelineRange = TimeSpan.FromHours(1);

    /// <summary>Tek zaman çizelgesi yanıtındaki en fazla parça; aşılırsa yanıt "truncated" işaretlenir.</summary>
    public const int MaxTimelineSegments = 5000;

    private const int MaxExportSegments = 10_000;

    public static IServiceCollection AddPlayback(this IServiceCollection services)
    {
        services.AddSingleton<ClipExporter>();
        services.AddSingleton(sp => new SegmentFileResolver(sp.GetRequiredService<IOptions<RecordingOptions>>().Value.RootPath));
        return services;
    }

    public static IEndpointRouteBuilder MapPlaybackApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/playback");

        api.MapGet("/config", (IOptions<RecordingOptions> options) => new
        {
            PublicLiveUrl = options.Value.Playback.PublicLiveUrl,
            MaxExportSeconds = options.Value.Playback.MaxExportDuration.TotalSeconds,
            MaxTimelineSeconds = MaxTimelineRange.TotalSeconds,
            ContiguityToleranceSeconds = PlaybackPlanner.ContiguityTolerance.TotalSeconds,
        });

        api.MapGet("/cameras", (IOptions<CameraOptions> cameras) =>
            cameras.Value.Enabled.Select(c => new { c.Id, Name = string.IsNullOrWhiteSpace(c.Name) ? c.Id : c.Name }));

        // ?from=2026-10-30T10:00:00Z&to=2026-10-30T11:00:00Z — verilmezse son bir saat.
        api.MapGet("/{cameraId}/timeline", async (
            string cameraId, string? from, string? to,
            IOptions<CameraOptions> cameras, SegmentRepository repository, TimeProvider time, CancellationToken cancellationToken) =>
        {
            if (!IsKnownCamera(cameras.Value, cameraId))
            {
                return UnknownCamera(cameraId);
            }

            if (!TryParseTime(from, "from", out var fromValue, out var error) || !TryParseTime(to, "to", out var toValue, out error))
            {
                return BadRequest(error);
            }

            var (rangeFrom, rangeTo) = (fromValue, toValue) switch
            {
                ({ } f, { } t) => (f, t),
                ({ } f, null) => (f, f + DefaultTimelineRange),
                (null, { } t) => (t - DefaultTimelineRange, t),
                _ => (time.GetUtcNow() - DefaultTimelineRange, time.GetUtcNow()),
            };

            if (rangeTo <= rangeFrom)
            {
                return BadRequest("Bitiş zamanı başlangıçtan sonra olmalı.");
            }

            if (rangeTo - rangeFrom > MaxTimelineRange)
            {
                return BadRequest($"Zaman çizelgesi aralığı en fazla {MaxTimelineRange.TotalHours:0} saat olabilir.");
            }

            var segments = await repository.FindForPlaybackAsync(cameraId, rangeFrom, rangeTo, MaxTimelineSegments, cancellationToken);
            var timeline = PlaybackPlanner.BuildTimeline(segments, rangeFrom, rangeTo);
            return Results.Ok(new
            {
                CameraId = cameraId,
                timeline.From,
                timeline.To,
                Segments = timeline.Items.Select(ToDto),
                Gaps = timeline.Gaps.Select(ToDto),
                Coverage = timeline.Coverage.Select(ToDto),
                timeline.RecordedSeconds,
                Truncated = segments.Count >= MaxTimelineSegments,
            });
        });

        // ?time=2026-10-30T10:15:00Z — o anı içeren parça ve parça içindeki konum; an bir boşluktaysa sonraki parçanın başı.
        api.MapGet("/{cameraId}/locate", async (
            string cameraId, string? time,
            IOptions<CameraOptions> cameras, SegmentRepository repository, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            if (!IsKnownCamera(cameras.Value, cameraId))
            {
                return UnknownCamera(cameraId);
            }

            if (!TryParseTime(time, "time", out var at, out var error) || at is null)
            {
                return BadRequest(error ?? "'time' parametresi gerekli.");
            }

            // İlk iki parça yeterli: o anı içeren (ya da sonraki) ve olası bir çakışma.
            var lookAheadEnd = (at.Value > clock.GetUtcNow() ? at.Value : clock.GetUtcNow()) + TimeSpan.FromDays(1);
            var segments = await repository.FindForPlaybackAsync(cameraId, at.Value, lookAheadEnd, 2, cancellationToken);
            var position = PlaybackPlanner.Locate(segments, at.Value);
            if (position is null)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Kayıt bulunamadı",
                    detail: "Bu andan sonra kayıt yok.");
            }

            return Results.Ok(new
            {
                Segment = ToDto(new TimelineItem(position.Segment.Id, position.Segment.StartedAt, position.Segment.EndedAt)),
                position.OffsetSeconds,
                position.Time,
            });
        });

        api.MapGet("/segments/{id:long}/media", async (
            long id, SegmentRepository repository, SegmentFileResolver files, CancellationToken cancellationToken) =>
        {
            var segment = await repository.FindByIdAsync(id, cancellationToken);
            var path = files.Resolve(segment?.FilePath);
            if (segment is null || path is null)
            {
                return Results.NotFound();
            }

            // Range desteği tarayıcının parça içinde atlamasını (seek) sağlar.
            return Results.File(path, "video/mp4", enableRangeProcessing: true, lastModified: segment.EndedAt);
        });

        // ?from=...&to=... — aralığı yeniden kodlamadan tek bir MP4 olarak indirir.
        api.MapGet("/{cameraId}/export", async (
            string cameraId, string? from, string? to,
            IOptions<CameraOptions> cameras, IOptions<RecordingOptions> options,
            SegmentRepository repository, SegmentFileResolver files, ClipExporter exporter,
            HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!IsKnownCamera(cameras.Value, cameraId))
            {
                return UnknownCamera(cameraId);
            }

            if (!TryParseTime(from, "from", out var fromValue, out var error) || !TryParseTime(to, "to", out var toValue, out error))
            {
                return BadRequest(error);
            }

            if (fromValue is null || toValue is null)
            {
                return BadRequest("'from' ve 'to' parametreleri gerekli.");
            }

            var invalid = ClipPlanner.Validate(fromValue.Value, toValue.Value, options.Value.Playback.MaxExportDuration);
            if (invalid is not null)
            {
                return BadRequest(invalid);
            }

            var segments = await repository.FindForPlaybackAsync(cameraId, fromValue.Value, toValue.Value, MaxExportSegments, cancellationToken);
            var entries = ClipPlanner.Plan(segments, fromValue.Value, toValue.Value, s => files.Resolve(s.FilePath));
            if (entries.Count == 0)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Kayıt bulunamadı",
                    detail: "Seçilen aralıkta kayıt yok.");
            }

            ExportedClipStream clip;
            try
            {
                clip = await exporter.ExportAsync(entries, cancellationToken);
            }
            catch (ClipExportException ex)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Klip oluşturulamadı",
                    detail: ex.Message);
            }

            // Akış yanıt gönderildikten sonra kapatılır; kapanınca geçici dosyalar silinir.
            context.Response.RegisterForDisposeAsync(clip);
            return Results.File(clip, "video/mp4", ClipPlanner.FileName(cameraId, fromValue.Value, toValue.Value));
        });

        return app;
    }

    private static object ToDto(TimelineItem item) => new
    {
        Id = item.SegmentId,
        item.Start,
        item.End,
        DurationSeconds = (item.End - item.Start).TotalSeconds,
        MediaUrl = $"/api/playback/segments/{item.SegmentId}/media",
    };

    private static object ToDto(TimeRange range) => new { range.Start, range.End };

    private static bool IsKnownCamera(CameraOptions cameras, string cameraId) =>
        cameras.Items.Any(c => string.Equals(c.Id, cameraId, StringComparison.Ordinal));

    private static IResult UnknownCamera(string cameraId) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Kamera bulunamadı", detail: $"'{cameraId}' adlı kamera tanımlı değil.");

    private static IResult BadRequest(string? detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Geçersiz istek", detail: detail);

    /// <summary>ISO 8601 zamanı ayrıştırır; saat dilimi belirtilmemişse UTC kabul edilir. Boş değer <c>null</c>'dır.</summary>
    internal static bool TryParseTime(string? value, string name, out DateTimeOffset? result, out string? error)
    {
        result = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            result = parsed;
            return true;
        }

        error = $"'{name}' geçerli bir ISO 8601 zamanı olmalı (ör. 2026-10-30T10:15:00Z).";
        return false;
    }
}
