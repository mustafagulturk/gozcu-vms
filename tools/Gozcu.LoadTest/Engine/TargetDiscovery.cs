using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Gozcu.LoadTest.Scenarios;

namespace Gozcu.LoadTest.Engine;

/// <summary>
/// Test başlamadan çalışan sistemden hedef veriyi okur: kamera listesi (/api/playback/cameras), son 24 saatin
/// parçaları (zaman çizelgesi) ve Range isteğiyle bir parça örneğinin dosya boyutları.
/// </summary>
internal sealed class TargetDiscovery
{
    private const int MaxCameras = 32;
    private const int MaxSizedSegments = 64;

    private readonly HttpClient _client;
    private readonly Uri _recordingUrl;
    private readonly TextWriter _log;

    public TargetDiscovery(HttpClient client, Uri recordingUrl, TextWriter log)
    {
        _client = client;
        _recordingUrl = recordingUrl;
        _log = log;
    }

    public async Task<TargetData> DiscoverAsync(DataNeeds needs, CancellationToken cancellationToken)
    {
        if (needs == DataNeeds.None)
        {
            return TargetData.Empty;
        }

        var cameras = await GetCamerasAsync(cancellationToken);
        if (cameras.Count == 0 || !needs.HasFlag(DataNeeds.Segments))
        {
            _log.WriteLine($"Keşif: {cameras.Count} kamera.");
            return new TargetData(cameras, []);
        }

        var now = DateTimeOffset.UtcNow;
        var segments = new List<SegmentInfo>();
        foreach (var camera in cameras)
        {
            segments.AddRange(await GetSegmentsAsync(camera, now, cancellationToken));
        }

        // Range isteği üretmek için dosya boyutu gerekir; rastgele bir örneğin boyutunu "bytes=0-0" ile öğren.
        var sample = segments.OrderBy(_ => Random.Shared.Next()).Take(MaxSizedSegments).ToList();
        var sized = new Dictionary<long, long>();
        foreach (var segment in sample)
        {
            if (await GetSizeAsync(segment.Id, cancellationToken) is { } size)
            {
                sized[segment.Id] = size;
            }
        }

        var all = segments
            .Select(s => sized.TryGetValue(s.Id, out var size) ? s with { SizeBytes = size } : s)
            .ToList();
        _log.WriteLine($"Keşif: {cameras.Count} kamera, son 24 saatte {all.Count} parça ({sized.Count} tanesinin boyutu ölçüldü).");
        return new TargetData(cameras, all);
    }

    private async Task<List<string>> GetCamerasAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await GetJsonAsync("/api/playback/cameras", cancellationToken);
            return doc is null
                ? []
                : doc.RootElement.EnumerateArray()
                    .Select(e => e.TryGetProperty("id", out var id) ? id.GetString() : null)
                    .OfType<string>()
                    .Take(MaxCameras)
                    .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            _log.WriteLine($"Uyarı: kamera listesi alınamadı ({_recordingUrl}api/playback/cameras): {ex.Message}");
            return [];
        }
    }

    private async Task<List<SegmentInfo>> GetSegmentsAsync(string camera, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var path = $"/api/playback/{Uri.EscapeDataString(camera)}/timeline?from={RequestFactory.Time(now.AddHours(-24).AddSeconds(1))}&to={RequestFactory.Time(now)}";
        try
        {
            using var doc = await GetJsonAsync(path, cancellationToken);
            if (doc is null || !doc.RootElement.TryGetProperty("segments", out var items))
            {
                return [];
            }

            return items.EnumerateArray()
                .Select(e => new SegmentInfo(
                    e.GetProperty("id").GetInt64(),
                    camera,
                    e.GetProperty("start").GetDateTimeOffset(),
                    e.GetProperty("end").GetDateTimeOffset()))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OperationCanceledException)
        {
            _log.WriteLine($"Uyarı: '{camera}' zaman çizelgesi alınamadı: {ex.Message}");
            return [];
        }
    }

    private async Task<long?> GetSizeAsync(long segmentId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(_recordingUrl, string.Create(CultureInfo.InvariantCulture, $"/api/playback/segments/{segmentId}/media")));
        request.Headers.Range = new RangeHeaderValue(0, 0);
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            // 206 ise Content-Range'deki toplam; sunucu Range'i yok saydıysa (200) Content-Length.
            return response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<JsonDocument?> GetJsonAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(new Uri(_recordingUrl, pathAndQuery), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _log.WriteLine($"Uyarı: {pathAndQuery} -> HTTP {(int)response.StatusCode}");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}
