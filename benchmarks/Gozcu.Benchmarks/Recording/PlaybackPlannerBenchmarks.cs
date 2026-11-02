using BenchmarkDotNet.Attributes;
using Gozcu.Benchmarks.Support;
using Gozcu.Infrastructure.Persistence;
using Gozcu.RecordingService.Playback;

namespace Gozcu.Benchmarks.Recording;

/// <summary>
/// <see cref="PlaybackPlanner"/>: geri oynatma sayfasının zaman çizelgesi (parçalar, boşluklar, kayıtlı kısımlar)
/// ve bir anın hangi parçaya düştüğünü bulma. Girdi, bir kameranın 1 dakikalık parçalardan oluşan tam bir günüdür
/// (1440 parça, araya serpiştirilmiş kısa kopmalarla). Her ikisi de her HTTP isteğinde çağrılır ve girdiyi
/// LINQ ile yeniden sıralar (O(n log n)); kullanıcı zaman çizelgesinde gezindikçe tekrarlandığından gün başına
/// süre ve ayrılan bellek (Allocated) yanıt süresine ve GC baskısına doğrudan yansır.
/// </summary>
[MemoryDiagnoser]
public class PlaybackPlannerBenchmarks
{
    private RecordingSegment[] _day = [];
    private DateTimeOffset _dayEnd;
    private DateTimeOffset _noon;

    [GlobalSetup]
    public void Setup()
    {
        _day = SyntheticRecordings.OneDayOfMinuteSegments();
        _dayEnd = SyntheticRecordings.DayStart.AddDays(1);
        _noon = SyntheticRecordings.DayStart.AddHours(12).AddSeconds(30);
    }

    /// <summary>Tam günün zaman çizelgesi (sayfa ilk açıldığında).</summary>
    [Benchmark(Baseline = true)]
    public int BuildTimelineFullDay() =>
        PlaybackPlanner.BuildTimeline(_day, SyntheticRecordings.DayStart, _dayEnd).Gaps.Count;

    /// <summary>Günün içinden 1 saatlik pencere (yakınlaştırılmış görünüm); filtre sıralamadan sonra uygulanır.</summary>
    [Benchmark]
    public int BuildTimelineOneHour() =>
        PlaybackPlanner.BuildTimeline(_day, _noon, _noon.AddHours(1)).Items.Count;

    /// <summary>Öğlen bir ana atlama (oynatıcıda arama/seek).</summary>
    [Benchmark]
    public double LocateNoon() => PlaybackPlanner.Locate(_day, _noon)?.OffsetSeconds ?? -1;
}
