using BenchmarkDotNet.Attributes;
using Gozcu.Media.Ffmpeg;

namespace Gozcu.Benchmarks.Media;

/// <summary>
/// <see cref="ConcatList.Build"/>: klip dışa aktarma ve geri oynatma için ffmpeg concat demuxer listesi
/// ("ffconcat version 1.0") üretme. Her girdi için yol tek tırnakla kaçırılır (<see cref="ConcatList.Quote"/>
/// bir alt dize ayırır) ve ilk/son girdiye inpoint/outpoint yazılır. <see cref="Entries"/>: 60, 1 dakikalık
/// parçalardan oluşan 1 saatlik en uzun klibi; 1440 ise tam bir günü temsil eder. Allocated, StringBuilder
/// büyümesi ve kaçırılmış yolların toplamıdır; girdi sayısıyla doğrusal kalmalıdır.
/// </summary>
[MemoryDiagnoser]
public class ConcatListBenchmarks
{
    private ConcatEntry[] _entries = [];

    /// <summary>Listedeki parça (dosya) sayısı.</summary>
    [Params(60, 1440)]
    public int Entries { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var start = new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
        _entries = new ConcatEntry[Entries];
        for (var i = 0; i < Entries; i++)
        {
            var path = "/recordings/cam1/2026-10-30/" + SegmentFileName.Format("cam1", start.AddMinutes(i));
            _entries[i] = new ConcatEntry(
                path,
                i == 0 ? TimeSpan.FromSeconds(12.5) : null,
                i == Entries - 1 ? TimeSpan.FromSeconds(47.25) : null);
        }
    }

    /// <summary>Tüm girdilerden liste metnini üretme.</summary>
    [Benchmark]
    public int Build() => ConcatList.Build(_entries).Length;
}
