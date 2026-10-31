using Gozcu.AnalysisService.Persistence;
using Gozcu.AnalysisService.Pipeline;
using Gozcu.AnalysisService.Snapshots;
using Gozcu.Contracts.Analysis;
using Gozcu.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Gozcu.AnalysisService.Api;

/// <summary>Analiz servisinin HTTP API'si: durum, son tespitler, anlık görüntü, tespit kareleri ve olaylar.</summary>
public static class AnalysisEndpoints
{
    private const int DefaultLimit = 100;

    public static IEndpointRouteBuilder MapAnalysisApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/analysis");

        api.MapGet("/status", (
            AnalyzerRegistry analyzers, DetectorProvider detector, IOptions<AnalysisOptions> options,
            InferenceGate gate, DetectionWriterMetrics writer, DetectionQueue queue) => new
            {
                Model = detector.Model,
                DetectorError = detector.Error?.Message,
                options.Value.FramesPerSecond,
                MaxConcurrentInferences = gate.MaxConcurrency,
                ActiveInferences = gate.Active,
                Events = new
                {
                    options.Value.Events.MinConfidence,
                    options.Value.Events.MinConsecutiveFrames,
                    EndAfterSeconds = options.Value.Events.EndAfter.TotalSeconds,
                },
                Writer = writer.Snapshot(queue.Dropped),
                Cameras = analyzers.Statuses(),
            });

        // Son analiz edilen karenin tespitleri. Kutular kaynak görüntüye göre 0..1 aralığındadır.
        api.MapGet("/cameras/{cameraId}/latest", (string cameraId, AnalyzerRegistry analyzers) =>
        {
            if (analyzers.Find(cameraId) is not { } analyzer)
            {
                return UnknownCamera(cameraId);
            }

            if (analyzer.Latest is not { } latest)
            {
                return NoFrameYet();
            }

            var result = latest.Result;
            return Results.Ok(new
            {
                CameraId = analyzer.CameraId,
                result.CapturedAt,
                InferenceMs = Math.Round(result.InferenceMs, 1),
                Source = new { Width = result.Geometry.SourceWidth, Height = result.Geometry.SourceHeight },
                // Veritabanındaki ve /detections yanıtındaki biçimle aynı: label, category, confidence, x, y, w, h.
                DetectionWriter.ToRecord(result).Detections,
            });
        });

        // Son analiz edilen kare: letterbox dolgusu kırpılmış, kutular çizilmiş 24 bit BMP.
        api.MapGet("/cameras/{cameraId}/snapshot.bmp", (string cameraId, AnalyzerRegistry analyzers, HttpContext context) =>
        {
            if (analyzers.Find(cameraId) is not { } analyzer)
            {
                return UnknownCamera(cameraId);
            }

            if (analyzer.Latest is not { } latest)
            {
                return NoFrameYet();
            }

            var bmp = SnapshotRenderer.RenderBmp(latest.Frame, latest.Result.Geometry, latest.Result.Detections);
            context.Response.Headers.CacheControl = "no-store";
            return Results.File(bmp, "image/bmp");
        });

        // ?cameraId=cam1&from=2026-10-31T10:00:00Z&to=...&limit=100 — en az bir tespit içeren kareler, yeniden eskiye.
        api.MapGet("/detections", async (
            string? cameraId, DateTimeOffset? from, DateTimeOffset? to, int? limit,
            DetectionRepository repository, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(cameraId))
            {
                return BadRequest("'cameraId' parametresi gerekli.");
            }

            return Results.Ok(await repository.FindFramesAsync(cameraId, from, to, limit ?? DefaultLimit, cancellationToken));
        });

        // ?cameraId=&category=Person|Vehicle&from=&to=&limit= — aralıkla kesişen olaylar, yeniden eskiye.
        api.MapGet("/events", async (
            string? cameraId, string? category, DateTimeOffset? from, DateTimeOffset? to, int? limit,
            DetectionRepository repository, CancellationToken cancellationToken) =>
        {
            ObjectCategory? parsedCategory = null;
            if (!string.IsNullOrWhiteSpace(category))
            {
                if (!Enum.TryParse<ObjectCategory>(category, ignoreCase: true, out var value) || !Enum.IsDefined(value))
                {
                    return BadRequest($"Geçersiz kategori '{category}'. Geçerli değerler: {string.Join(", ", Enum.GetNames<ObjectCategory>())}.");
                }

                parsedCategory = value;
            }

            return Results.Ok(await repository.FindEventsAsync(
                string.IsNullOrWhiteSpace(cameraId) ? null : cameraId, parsedCategory, from, to, limit ?? DefaultLimit, cancellationToken));
        });

        return app;
    }

    private static IResult UnknownCamera(string cameraId) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Kamera bulunamadı", detail: $"'{cameraId}' kamerası analiz edilmiyor.");

    private static IResult NoFrameYet() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Kare yok", detail: "Bu kamerada henüz analiz edilmiş kare yok.");

    private static IResult BadRequest(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Geçersiz istek", detail: detail);
}
