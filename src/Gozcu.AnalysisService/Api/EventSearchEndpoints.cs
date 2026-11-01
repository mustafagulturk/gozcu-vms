using System.Globalization;
using Gozcu.Contracts.Analysis;
using Gozcu.Infrastructure.Cameras;
using Gozcu.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;

namespace Gozcu.AnalysisService.Api;

/// <summary>
/// Olay arama ve geri oynatma için tespit kareleri API'si:
/// <c>GET /api/analysis/events/search</c> ve <c>GET /api/analysis/cameras/{cameraId}/frames</c>.
/// Parametreler sorgu dizesinden elle ayrıştırılır; böylece her geçersiz değer açıklamalı bir ProblemDetails (400) döner.
/// </summary>
public static class EventSearchEndpoints
{
    public static IEndpointRouteBuilder MapEventSearchApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/analysis");

        // ?cameraId=cam1&cameraId=cam2&category=Person&from=&to=&minConfidence=0.5&minDurationSeconds=3&cursor=&limit=50
        api.MapGet("/events/search", async (
            HttpRequest request, DetectionRepository repository, [FromServices] ICameraCatalog catalog, TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var now = time.GetUtcNow();
            if (!EventSearchParameters.TryParse(request.Query, now, out var query, out var error))
            {
                return BadRequest(error);
            }

            var page = await repository.SearchEventsAsync(query, cancellationToken);
            return Results.Ok(new EventSearchResponse(
                page.Items.Select(e => EventView.From(e, CameraName(catalog, e.CameraId), now)).ToList(),
                page.Next?.Encode()));
        });

        // ?from=&to=&limit= — [from, to) aralığındaki tespit kareleri, ESKİDEN YENİYE.
        api.MapGet("/cameras/{cameraId}/frames", async (
            string cameraId, HttpRequest request, DetectionRepository repository, CancellationToken cancellationToken) =>
        {
            if (!FrameRangeParameters.TryParse(request.Query, out var range, out var error))
            {
                return BadRequest(error);
            }

            // Bilinmeyen (ya da silinmiş) kamera için boş liste döner: kareleri saklama süresi boyunca okunabilir.
            var frames = await repository.FindFramesAscendingAsync(cameraId, range.From, range.To, range.Limit, cancellationToken);
            return Results.Ok(frames.Select(FrameView.From).ToList());
        });

        return app;
    }

    private static string CameraName(ICameraCatalog catalog, string cameraId) =>
        catalog.Find(cameraId)?.Name is { } name && !string.IsNullOrWhiteSpace(name) ? name : cameraId;

    private static IResult BadRequest(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Geçersiz istek", detail: detail);
}

/// <summary>Olay arama yanıtı. <see cref="NextCursor"/> null ise başka sayfa yoktur.</summary>
public sealed record EventSearchResponse(IReadOnlyList<EventView> Items, string? NextCursor);

/// <summary>Arama sonucundaki olay. Süren olayda <see cref="EndedAt"/> null, süre ise şimdiye kadar geçen süredir.</summary>
public sealed record EventView(
    Guid Id,
    string CameraId,
    string CameraName,
    string Category,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    double DurationSeconds,
    int MaxCount,
    float PeakConfidence,
    int FrameCount)
{
    public static EventView From(DetectionEventRecord e, string cameraName, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(e);
        var end = e.EndedAt ?? (now > e.StartedAt ? now : e.StartedAt);
        return new EventView(
            e.Id,
            e.CameraId,
            cameraName,
            e.Category.ToString(),
            e.StartedAt,
            e.EndedAt,
            Math.Round((end - e.StartedAt).TotalSeconds, 3),
            e.MaxCount,
            e.PeakConfidence,
            e.FrameCount);
    }
}

/// <summary>Geri oynatmada kutu çizimi için bir tespit karesi.</summary>
public sealed record FrameView(DateTimeOffset CapturedAt, IReadOnlyList<FrameDetectionView> Detections)
{
    public static FrameView From(DetectionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return new FrameView(
            frame.CapturedAt,
            frame.Detections.Select(d => new FrameDetectionView(d.Label, d.Category.ToString(), d.Confidence, d.X, d.Y, d.W, d.H)).ToList());
    }
}

/// <summary>Kaynak görüntüye göre 0..1 aralığında normalize edilmiş kutu (jsonb biçimiyle aynı alanlar).</summary>
public sealed record FrameDetectionView(string Label, string Category, float Confidence, float X, float Y, float W, float H);

/// <summary><c>/events/search</c> sorgu parametrelerinin doğrulanması.</summary>
public static class EventSearchParameters
{
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;

    /// <summary>Tek aramada verilebilecek en fazla kamera kimliği.</summary>
    public const int MaxCameras = 100;

    public static bool TryParse(IQueryCollection query, DateTimeOffset now, out EventSearchQuery result, out string error)
    {
        ArgumentNullException.ThrowIfNull(query);
        result = null!;
        error = string.Empty;

        // Kamera kimlikleri tekrarlanarak (cameraId=a&cameraId=b) ya da virgülle (cameraId=a,b) verilebilir.
        var cameraIds = query["cameraId"]
            .SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (cameraIds.Count > MaxCameras)
        {
            error = $"En fazla {MaxCameras} kamera seçilebilir.";
            return false;
        }

        ObjectCategory? category = null;
        if (!QueryParsing.TrySingle(query, "category", out var categoryText, ref error))
        {
            return false;
        }

        if (categoryText is not null)
        {
            if (!Enum.TryParse<ObjectCategory>(categoryText, ignoreCase: true, out var value) || !Enum.IsDefined(value)
                || int.TryParse(categoryText, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                error = $"Geçersiz kategori '{categoryText}'. Geçerli değerler: {string.Join(", ", Enum.GetNames<ObjectCategory>())}.";
                return false;
            }

            category = value;
        }

        if (!QueryParsing.TryTime(query, "from", out var from, ref error)
            || !QueryParsing.TryTime(query, "to", out var to, ref error))
        {
            return false;
        }

        if (from is not null && to is not null && to <= from)
        {
            error = "'to' zamanı 'from' zamanından sonra olmalı.";
            return false;
        }

        if (!QueryParsing.TryDouble(query, "minConfidence", 0, 1, out var minConfidence, ref error)
            || !QueryParsing.TryDouble(query, "minDurationSeconds", 0, TimeSpan.FromDays(365).TotalSeconds, out var minDuration, ref error)
            || !QueryParsing.TryInt(query, "limit", 1, MaxLimit, out var limit, ref error))
        {
            return false;
        }

        if (!QueryParsing.TrySingle(query, "cursor", out var cursorText, ref error))
        {
            return false;
        }

        EventSearchCursor? cursor = null;
        if (cursorText is not null)
        {
            if (!EventSearchCursor.TryDecode(cursorText, out var decoded))
            {
                error = "'cursor' geçersiz. Önceki yanıttaki 'nextCursor' değerini olduğu gibi gönderin.";
                return false;
            }

            cursor = decoded;
        }

        result = new EventSearchQuery(
            cameraIds,
            category,
            from,
            to,
            minConfidence is null ? null : (float)minConfidence.Value,
            minDuration is null ? null : TimeSpan.FromSeconds(minDuration.Value),
            now,
            cursor,
            limit ?? DefaultLimit);
        return true;
    }
}

/// <summary><c>/cameras/{cameraId}/frames</c> için doğrulanmış aralık.</summary>
public sealed record FrameRange(DateTimeOffset From, DateTimeOffset To, int Limit);

/// <summary><c>/cameras/{cameraId}/frames</c> sorgu parametrelerinin doğrulanması.</summary>
public static class FrameRangeParameters
{
    /// <summary>Tek istekte okunabilecek en uzun aralık; oynatıcı kareleri pencere pencere yükler.</summary>
    public static readonly TimeSpan MaxRange = TimeSpan.FromMinutes(15);

    public const int DefaultLimit = 2000;

    public const int MaxLimit = DetectionRepository.MaxFramesAscending;

    public static bool TryParse(IQueryCollection query, out FrameRange result, out string error)
    {
        ArgumentNullException.ThrowIfNull(query);
        result = null!;
        error = string.Empty;

        if (!QueryParsing.TryTime(query, "from", out var from, ref error)
            || !QueryParsing.TryTime(query, "to", out var to, ref error)
            || !QueryParsing.TryInt(query, "limit", 1, MaxLimit, out var limit, ref error))
        {
            return false;
        }

        if (from is null || to is null)
        {
            error = "'from' ve 'to' parametreleri gerekli.";
            return false;
        }

        if (to <= from)
        {
            error = "'to' zamanı 'from' zamanından sonra olmalı.";
            return false;
        }

        if (to.Value - from.Value > MaxRange)
        {
            error = $"Aralık en fazla {MaxRange.TotalMinutes:0} dakika olabilir.";
            return false;
        }

        result = new FrameRange(from.Value, to.Value, limit ?? DefaultLimit);
        return true;
    }
}

/// <summary>Sorgu dizesi değerlerini ayrıştıran ortak yardımcılar. Boş değer "verilmedi" sayılır.</summary>
internal static class QueryParsing
{
    public static bool TrySingle(IQueryCollection query, string name, out string? value, ref string error)
    {
        value = null;
        StringValues values = query[name];
        var nonEmpty = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (nonEmpty.Count > 1)
        {
            error = $"'{name}' parametresi yalnızca bir kez verilebilir.";
            return false;
        }

        value = nonEmpty.Count == 1 ? nonEmpty[0]!.Trim() : null;
        return true;
    }

    /// <summary>ISO 8601 zaman; saat dilimi belirtilmemişse UTC kabul edilir.</summary>
    public static bool TryTime(IQueryCollection query, string name, out DateTimeOffset? value, ref string error)
    {
        value = null;
        if (!TrySingle(query, name, out var text, ref error))
        {
            return false;
        }

        if (text is null)
        {
            return true;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            value = parsed;
            return true;
        }

        error = $"'{name}' geçerli bir ISO 8601 zamanı olmalı (ör. 2026-10-31T10:15:00Z).";
        return false;
    }

    public static bool TryDouble(IQueryCollection query, string name, double min, double max, out double? value, ref string error)
    {
        value = null;
        if (!TrySingle(query, name, out var text, ref error))
        {
            return false;
        }

        if (text is null)
        {
            return true;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed)
            || parsed < min || parsed > max)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"'{name}' {min}..{max} aralığında bir sayı olmalı.");
            return false;
        }

        value = parsed;
        return true;
    }

    public static bool TryInt(IQueryCollection query, string name, int min, int max, out int? value, ref string error)
    {
        value = null;
        if (!TrySingle(query, name, out var text, ref error))
        {
            return false;
        }

        if (text is null)
        {
            return true;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < min || parsed > max)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"'{name}' {min}..{max} aralığında bir tam sayı olmalı.");
            return false;
        }

        value = parsed;
        return true;
    }
}
