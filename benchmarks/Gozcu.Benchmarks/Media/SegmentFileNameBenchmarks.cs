using BenchmarkDotNet.Attributes;
using Gozcu.Media.Ffmpeg;

namespace Gozcu.Benchmarks.Media;

/// <summary>
/// <see cref="SegmentFileName"/>: kayıt parçası dosya adının ("cam1_20261030T101500Z.mp4") üretilmesi ve
/// ayrıştırılması. Ayrıştırma, açılışta disk ile veritabanını uzlaştırırken ve saklama politikası diski tararken
/// klasördeki her dosya için çağrılır; 30 gün × 1440 parça × kamera sayısı kadar dosya olabileceğinden kaynak
/// üretimli (GeneratedRegex) desen ve <c>TryParseExact</c> maliyeti açılış süresine doğrudan yansır.
/// Biçimlendirme ise klip dışa aktarma adlarında ve ffmpeg deseninde kullanılır.
/// </summary>
[MemoryDiagnoser]
public class SegmentFileNameBenchmarks
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 10, 15, 0, TimeSpan.Zero);

    private readonly string _fileName = SegmentFileName.Format("cam1", Start);
    private readonly string _fullPath = "/recordings/cam1/2026-10-30/" + SegmentFileName.Format("cam1", Start);
    private readonly string _foreignFile = "/recordings/cam1/2026-10-30/notes.txt";

    /// <summary>Kamera kimliği ve UTC başlangıçtan dosya adı üretme.</summary>
    [Benchmark]
    public string Format() => SegmentFileName.Format("cam1", Start);

    /// <summary>Yalın dosya adının ayrıştırılması (en sık yol).</summary>
    [Benchmark(Baseline = true)]
    public bool TryParseFileName() => SegmentFileName.TryParse(_fileName, out _, out _);

    /// <summary>Tam yolun ayrıştırılması (<c>Path.GetFileName</c> dahil), disk taramasındaki gibi.</summary>
    [Benchmark]
    public bool TryParseFullPath() => SegmentFileName.TryParse(_fullPath, out _, out _);

    /// <summary>Desene uymayan bir dosyanın reddedilmesi; erken çıkışın ucuz olduğunu doğrular.</summary>
    [Benchmark]
    public bool TryParseForeignFile() => SegmentFileName.TryParse(_foreignFile, out _, out _);
}
