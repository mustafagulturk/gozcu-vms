using System.Globalization;

namespace Gozcu.LoadTest.Scenarios;

/// <summary>
/// Servislerin gerçek rotalarına göre istek tanımları üretir. Saf işlevlerdir: aynı girdi (rastgele tohumu dahil)
/// aynı isteği verir, bu yüzden birim testleri yazılabilir.
/// </summary>
internal static class RequestFactory
{
    /// <summary>Parça medyası senaryosunda istenen bayt aralığı (1 MiB).</summary>
    public const long RangeLength = 1024 * 1024;

    /// <summary>Dışa aktarılan klip uzunluğu.</summary>
    public static readonly TimeSpan ExportLength = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    // StreamingService: cameras.MapGet("/") — kamera sağlık durumları listesi.
    public static RequestSpec CameraStatus(string label) =>
        new(label, ServiceTarget.Streaming, "/api/cameras");

    // RecordingService: api.MapGet("/status") — kaydedici durumları ve ölçümler.
    public static RequestSpec RecordingStatus(string label) =>
        new(label, ServiceTarget.Recording, "/api/recordings/status");

    // AnalysisService: api.MapGet("/status").
    public static RequestSpec AnalysisStatus(string label) =>
        new(label, ServiceTarget.Analysis, "/api/analysis/status");

    /// <summary>
    /// /api/playback/{cameraId}/timeline?from=&amp;to= — rastgele kamera. Pencere 24 saatten kısaysa son 24 saat içinde
    /// rastgele bir yere kaydırılır (operatör geçmişte gezinir); 24 saatlik pencere her zaman "şimdi"de biter.
    /// </summary>
    public static RequestSpec Timeline(string label, ScenarioInput input, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(input);
        var camera = Pick(input.Data.CameraIds, input.Random);
        var slack = Day - window;
        var to = slack > TimeSpan.Zero ? input.Now - (slack * input.Random.NextDouble()) : input.Now;
        to = Truncate(to);
        var from = to - window;
        return new(label, ServiceTarget.Recording,
            $"/api/playback/{Esc(camera)}/timeline?from={Time(from)}&to={Time(to)}");
    }

    /// <summary>/api/playback/{cameraId}/locate?time= — bilinen bir parçanın içinden rastgele an; parça yoksa son 24 saat.</summary>
    public static RequestSpec Locate(string label, ScenarioInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        string camera;
        DateTimeOffset at;
        if (input.Data.Segments.Count > 0)
        {
            var segment = Pick(input.Data.Segments, input.Random);
            camera = segment.CameraId;
            at = segment.Start + ((segment.End - segment.Start) * input.Random.NextDouble());
        }
        else
        {
            camera = Pick(input.Data.CameraIds, input.Random);
            at = input.Now - (Day * input.Random.NextDouble());
        }

        return new(label, ServiceTarget.Recording, $"/api/playback/{Esc(camera)}/locate?time={Time(Truncate(at))}");
    }

    /// <summary>/api/playback/segments/{id}/media — rastgele konumdan 1 MiB'lık Range isteği (tarayıcının atlaması gibi).</summary>
    public static RequestSpec SegmentMedia(string label, ScenarioInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var segment = Pick(input.Data.SizedSegments, input.Random);
        var (start, end) = PickRange(segment.SizeBytes!.Value, RangeLength, input.Random.NextDouble());
        return new(label, ServiceTarget.Recording,
            string.Create(CultureInfo.InvariantCulture, $"/api/playback/segments/{segment.Id}/media"), start, end);
    }

    /// <summary>/api/analysis/detections?cameraId=&amp;from=&amp;to=&amp;limit=100 — rastgele kamera, son bir saat.</summary>
    public static RequestSpec Detections(string label, ScenarioInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var camera = Pick(input.Data.CameraIds, input.Random);
        var to = Truncate(input.Now);
        return new(label, ServiceTarget.Analysis,
            $"/api/analysis/detections?cameraId={Esc(camera)}&from={Time(to.AddHours(-1))}&to={Time(to)}&limit=100");
    }

    /// <summary>/api/analysis/events?from=&amp;to=&amp;limit=100 — tüm kameralar, son bir saat (operatör olay listesi).</summary>
    public static RequestSpec Events(string label, ScenarioInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var to = Truncate(input.Now);
        return new(label, ServiceTarget.Analysis,
            $"/api/analysis/events?from={Time(to.AddHours(-1))}&to={Time(to)}&limit=100");
    }

    /// <summary>/api/playback/{cameraId}/export?from=&amp;to= — bilinen bir parçanın başlangıcından 30 sn'lik klip.</summary>
    public static RequestSpec Export(string label, ScenarioInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var segment = Pick(input.Data.Segments, input.Random);
        var duration = segment.End - segment.Start;
        var room = duration > ExportLength ? duration - ExportLength : TimeSpan.Zero;
        var from = Truncate(segment.Start + (room * input.Random.NextDouble()));
        return new(label, ServiceTarget.Recording,
            $"/api/playback/{Esc(segment.CameraId)}/export?from={Time(from)}&to={Time(from + ExportLength)}");
    }

    /// <summary>
    /// <paramref name="size"/> baytlık dosyada <paramref name="length"/> baytlık aralık seçer. <paramref name="roll"/> [0,1)
    /// başlangıç konumunu belirler. Dosya aralıktan küçükse tüm dosya istenir. Sonuç kapsayıcıdır (HTTP Range gibi).
    /// </summary>
    public static (long Start, long End) PickRange(long size, long length, double roll)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Dosya boyutu pozitif olmalı.");
        }

        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Aralık uzunluğu pozitif olmalı.");
        }

        var maxStart = Math.Max(0, size - length);
        var start = (long)Math.Floor(Math.Clamp(roll, 0, 1) * (maxStart + 1));
        start = Math.Min(start, maxStart);
        var end = Math.Min(size, start + length) - 1;
        return (start, end);
    }

    /// <summary>Saniyeye kırpılmış UTC ISO 8601 ("2026-10-30T10:15:00Z"), sorgu için kaçışlı.</summary>
    public static string Time(DateTimeOffset value) =>
        Uri.EscapeDataString(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));

    private static DateTimeOffset Truncate(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);

    private static string Esc(string value) => Uri.EscapeDataString(value);

    private static T Pick<T>(IReadOnlyList<T> items, Random random)
    {
        if (items.Count == 0)
        {
            throw new InvalidOperationException("Seçilecek öğe yok; hedef veri keşfedilemedi.");
        }

        return items[random.Next(items.Count)];
    }
}
