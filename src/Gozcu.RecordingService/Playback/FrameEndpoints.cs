using System.Globalization;
using Gozcu.Infrastructure.Persistence;

namespace Gozcu.RecordingService.Playback;

/// <summary>
/// Kayıttan tek kare: <c>GET /api/playback/{cameraId}/frame.jpg?time=&lt;ISO&gt;&amp;width=320</c>.
/// Olay listesindeki küçük resimler için. Kamera kataloğuna bakılmaz: silinmiş bir kameranın kaydı da saklama
/// süresi boyunca görüntülenebilir; o anı içeren kayıt yoksa 404 döner.
/// </summary>
public static class FrameEndpoints
{
    public const int DefaultWidth = 320;

    public const int MinWidth = 64;

    public const int MaxWidth = 1280;

    /// <summary>Kayıtlı bir anın karesi değişmez; tarayıcı ve ara önbellekler bir gün saklayabilir.</summary>
    public const string CacheControl = "public, max-age=86400";

    public static IEndpointRouteBuilder MapPlaybackFrameApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/playback/{cameraId}/frame.jpg", async (
            string cameraId, string? time, string? width,
            SegmentRepository repository, SegmentFileResolver files, FrameExtractor extractor,
            HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!TryParse(time, width, out var at, out var frameWidth, out var error))
            {
                return BadRequest(error);
            }

            // An iki parça arasındaki küçük bir boşluğa (anahtar kare hizalaması) düşerse sonraki parçanın ilk karesi
            // kullanılır; daha uzun boşluklarda kayıt yok sayılır.
            var segments = await repository.FindForPlaybackAsync(
                cameraId, at, at + PlaybackPlanner.ContiguityTolerance, 2, cancellationToken);
            var position = PlaybackPlanner.Locate(segments, at);
            var path = files.Resolve(position?.Segment.FilePath);
            if (position is null || path is null)
            {
                return NoRecording();
            }

            byte[]? jpeg;
            try
            {
                jpeg = await extractor.ExtractJpegAsync(path, TimeSpan.FromSeconds(position.OffsetSeconds), frameWidth, cancellationToken);
            }
            catch (FrameExtractionException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Kare çıkarılamadı", detail: ex.Message);
            }

            if (jpeg is null)
            {
                return NoRecording();
            }

            context.Response.Headers.CacheControl = CacheControl;
            return Results.File(jpeg, "image/jpeg");
        });

        return app;
    }

    /// <summary><c>time</c> (zorunlu, ISO 8601) ve <c>width</c> (<see cref="MinWidth"/>..<see cref="MaxWidth"/>) parametrelerini doğrular.</summary>
    public static bool TryParse(string? time, string? width, out DateTimeOffset at, out int frameWidth, out string error)
    {
        at = default;
        frameWidth = DefaultWidth;
        error = string.Empty;

        if (!PlaybackEndpoints.TryParseTime(time, "time", out var parsed, out var timeError) || parsed is null)
        {
            error = timeError ?? "'time' parametresi gerekli.";
            return false;
        }

        at = parsed.Value;
        if (!string.IsNullOrWhiteSpace(width)
            && (!int.TryParse(width, NumberStyles.Integer, CultureInfo.InvariantCulture, out frameWidth) || frameWidth < MinWidth || frameWidth > MaxWidth))
        {
            frameWidth = DefaultWidth;
            error = $"'width' {MinWidth}..{MaxWidth} aralığında bir tam sayı olmalı.";
            return false;
        }

        return true;
    }

    private static IResult NoRecording() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Kayıt bulunamadı", detail: "Bu anda kayıt yok.");

    private static IResult BadRequest(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Geçersiz istek", detail: detail);
}
