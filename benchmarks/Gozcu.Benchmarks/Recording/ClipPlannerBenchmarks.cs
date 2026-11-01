using BenchmarkDotNet.Attributes;
using Gozcu.Benchmarks.Support;
using Gozcu.Infrastructure.Persistence;
using Gozcu.Media.Ffmpeg;
using Gozcu.RecordingService.Playback;

namespace Gozcu.Benchmarks.Recording;

/// <summary>
/// <see cref="ClipPlanner.Plan"/>: dışa aktarılacak klip için concat girdilerinin (aralıkla kesişen parçalar,
/// ilk parçada inpoint, son parçada outpoint) planlanması. Girdi bir kameranın tam günüdür (1440 parça);
/// planlayıcı tüm parçaları sıraladığı için maliyet klibin uzunluğundan çok gündeki parça sayısına bağlıdır.
/// İzin verilen en uzun klip (1 saat) ve tipik bir olay klibi (5 dakika) ölçülür; ayrıca planın
/// <see cref="ConcatList.Build"/> ile liste metnine dönüştürülmesi dahil uçtan uca süre verilir.
/// </summary>
[MemoryDiagnoser]
public class ClipPlannerBenchmarks
{
    private RecordingSegment[] _day = [];
    private DateTimeOffset _from;

    [GlobalSetup]
    public void Setup()
    {
        _day = SyntheticRecordings.OneDayOfMinuteSegments();
        _from = SyntheticRecordings.DayStart.AddHours(14).AddSeconds(12.5);
    }

    /// <summary>5 dakikalık olay klibi planı.</summary>
    [Benchmark]
    public int PlanFiveMinutes() => ClipPlanner.Plan(_day, _from, _from.AddMinutes(5)).Count;

    /// <summary>1 saatlik (en uzun) klip planı.</summary>
    [Benchmark(Baseline = true)]
    public int PlanOneHour() => ClipPlanner.Plan(_day, _from, _from.AddHours(1)).Count;

    /// <summary>1 saatlik klip: doğrulama, plan ve ffmpeg concat listesi metni (dışa aktarma isteğinin CPU kısmı).</summary>
    [Benchmark]
    public int PlanAndBuildConcatListOneHour()
    {
        var to = _from.AddHours(1);
        if (ClipPlanner.Validate(_from, to, TimeSpan.FromHours(1)) is not null)
        {
            return -1;
        }

        var entries = ClipPlanner.Plan(_day, _from, to, s => "/recordings/" + s.FilePath);
        return ConcatList.Build(entries).Length;
    }
}
