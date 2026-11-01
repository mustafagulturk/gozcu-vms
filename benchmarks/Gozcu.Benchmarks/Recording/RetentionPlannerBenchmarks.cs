using BenchmarkDotNet.Attributes;
using Gozcu.RecordingService;
using Gozcu.RecordingService.Retention;

namespace Gozcu.Benchmarks.Recording;

/// <summary>
/// <see cref="RetentionPlanner.Plan"/>: kota aşıldığında ya da boş alan azaldığında en eski parçalardan başlayarak
/// silinecekleri seçme. Saklama servisi bunu birkaç saniyede bir diskteki tüm parçalar üzerinde çalıştırır.
/// 100.000 parça, 1 dakikalık parçalarla ~10 kamera × 7 günlük bir kayıt arşivine karşılık gelir.
/// <para>
/// Planlayıcı önce toplam boyutu hesaplar, sonra tüm parçaları zamana ve yola göre sıralar. LINQ <c>OrderBy</c>
/// ilk öğeyi vermeden önce tüm diziyi sıraladığından, hiçbir şey silinmeyecek olsa bile (<see cref="UnderQuota"/>)
/// her turda O(n log n) iş ve n boyutlu ara diziler ayrılır; bu ölçüm o sabit maliyeti görünür kılar.
/// <see cref="OverQuota"/> ise arşivin ~%10'unun silinmesi gereken durumdur.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class RetentionPlannerBenchmarks
{
    private const long SegmentBytes = 8_000_000;

    private readonly HashSet<string> _protected = new(StringComparer.Ordinal);
    private StoredSegment[] _segments = [];
    private RetentionOptions _underQuota = new();
    private RetentionOptions _overQuota = new();

    /// <summary>Diskteki parça sayısı.</summary>
    [Params(10_000, 100_000)]
    public int Segments { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        const int cameras = 10;
        var start = new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        var random = new Random(42);
        _segments = new StoredSegment[Segments];
        for (var i = 0; i < Segments; i++)
        {
            var camera = $"cam{(i % cameras) + 1}";
            var startedAt = start.AddMinutes(i / cameras);
            _segments[i] = new StoredSegment(
                $"/recordings/{camera}/{startedAt:yyyy-MM-dd}/{camera}_{startedAt:yyyyMMdd'T'HHmmss'Z'}.mp4",
                camera,
                startedAt,
                SegmentBytes + random.Next(-500_000, 500_000));
        }

        // Dizin taraması sırası zamana göre değildir; planlayıcının gerçekten sıralama yapması için karıştır.
        random.Shuffle(_segments);

        // Her kameranın yazılmakta olan son parçası korunur.
        foreach (var segment in _segments.OrderByDescending(s => s.StartedAt).Take(cameras))
        {
            _protected.Add(segment.FilePath);
        }

        var total = _segments.Sum(s => s.SizeBytes);
        _underQuota = new RetentionOptions { MaxTotalBytes = total * 2, MinFreeBytes = 0 };
        _overQuota = new RetentionOptions { MaxTotalBytes = total - (total / 10), MinFreeBytes = 0 };
    }

    /// <summary>Kota içinde: hiçbir parça silinmez, yalnızca toplam ve sıralama maliyeti.</summary>
    [Benchmark(Baseline = true)]
    public int UnderQuota() => RetentionPlanner.Plan(_segments, long.MaxValue, _underQuota, _protected).Count;

    /// <summary>Kota %10 aşılmış: en eski ~%10 parça silinmek üzere seçilir.</summary>
    [Benchmark]
    public int OverQuota() => RetentionPlanner.Plan(_segments, long.MaxValue, _overQuota, _protected).Count;
}
