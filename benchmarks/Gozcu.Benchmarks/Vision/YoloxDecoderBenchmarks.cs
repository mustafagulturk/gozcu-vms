using BenchmarkDotNet.Attributes;
using Gozcu.Vision;

namespace Gozcu.Benchmarks.Vision;

/// <summary>
/// <see cref="YoloxDecoder.Decode"/>: 416x416 girişli YOLOX'un ham çıktısını ([1, 3549, 85] = 301.665 float)
/// aday tespitlere çevirme. Her karede bir kez, çıkarımdan sonra çalışır. Maliyetin çoğu 3549 satırın nesnellik
/// değerine bakmak; eşiği geçen her satır için ayrıca 80 sınıf skoru taranır, <c>exp</c> hesaplanır ve bir
/// <see cref="Detection"/> nesnesi ayrılır. Bu yüzden <see cref="HotAnchors"/> ile eşiği geçen satır sayısı
/// değiştirilir: 0 boş bir sahne, 30 tipik bir otopark, 300 kalabalık bir sahnedir. Allocated sütunu aday sayısıyla
/// doğrusal artmalıdır; boş sahnede yalnızca boş liste ayrılmalıdır.
/// </summary>
[MemoryDiagnoser]
public class YoloxDecoderBenchmarks
{
    private const int InputSize = 416;
    private const float ConfidenceThreshold = 0.35f;

    private readonly LetterboxGeometry _geometry = new(InputSize, InputSize, 1920, 1080);
    private float[] _output = [];

    /// <summary>Nesnellik değeri güven eşiğinin üstünde olan çapa (satır) sayısı.</summary>
    [Params(0, 30, 300)]
    public int HotAnchors { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var anchors = YoloxDecoder.AnchorCount(InputSize, InputSize);
        _output = new float[anchors * YoloxDecoder.ValuesPerAnchor];
        var random = new Random(42);
        for (var row = 0; row < anchors; row++)
        {
            var values = _output.AsSpan(row * YoloxDecoder.ValuesPerAnchor, YoloxDecoder.ValuesPerAnchor);
            values[0] = random.NextSingle();
            values[1] = random.NextSingle();
            values[2] = (random.NextSingle() * 3f) - 1f;
            values[3] = (random.NextSingle() * 3f) - 1f;
            values[4] = random.NextSingle() * 0.05f;
            for (var c = 5; c < values.Length; c++)
            {
                values[c] = random.NextSingle() * 0.1f;
            }
        }

        // Eşiği geçen satırlar çıktıya dağılsın; yarısı insan (0), yarısı otomobil (2) sınıfına güçlü skor alır.
        var step = Math.Max(1, anchors / Math.Max(1, HotAnchors));
        for (var i = 0; i < HotAnchors; i++)
        {
            var row = i * step % anchors;
            var values = _output.AsSpan(row * YoloxDecoder.ValuesPerAnchor, YoloxDecoder.ValuesPerAnchor);
            values[4] = 0.6f + (random.NextSingle() * 0.39f);
            values[5 + (i % 2 == 0 ? 0 : 2)] = 0.7f + (random.NextSingle() * 0.29f);
        }
    }

    /// <summary>Tek karenin ham çıktısının kod çözümü (NMS hariç).</summary>
    [Benchmark]
    public int Decode() =>
        YoloxDecoder.Decode(_output, InputSize, InputSize, ConfidenceThreshold, _geometry).Count;
}
