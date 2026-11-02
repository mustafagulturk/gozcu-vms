using BenchmarkDotNet.Attributes;
using Gozcu.Vision;

namespace Gozcu.Benchmarks.Vision;

/// <summary>
/// <see cref="LetterboxGeometry.ToNormalized"/>: model pikselindeki bir kutuyu kaynak kareye göre 0..1 aralığına
/// çevirme. Kod çözücü eşiği geçen her aday için bir kez çağırır. <c>Scale</c>, <c>ContentWidth</c> ve
/// <c>ContentHeight</c> önbelleğe alınmayan hesaplanan özellikler olduğundan her çağrıda bölme ve yuvarlama
/// tekrarlanır; bu ölçüm o tekrarın gerçekten ihmal edilebilir olup olmadığını gösterir. Bir çağrının süresini
/// görmek için 1000 kutu dönüştürülür ve <c>OperationsPerInvoke</c> ile Mean kutu başına verilir.
/// Allocated 0 B olmalıdır (<see cref="BoundingBox"/> bir struct'tır).
/// </summary>
[MemoryDiagnoser]
public class LetterboxGeometryBenchmarks
{
    private const int BoxCount = 1000;

    private readonly LetterboxGeometry _geometry = new(416, 416, 1920, 1080);
    private readonly float[] _boxes = new float[BoxCount * 4];

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(42);
        for (var i = 0; i < _boxes.Length; i += 4)
        {
            _boxes[i] = random.NextSingle() * 400f;
            _boxes[i + 1] = random.NextSingle() * 230f;
            _boxes[i + 2] = 10f + (random.NextSingle() * 100f);
            _boxes[i + 3] = 10f + (random.NextSingle() * 100f);
        }
    }

    /// <summary>Kutu başına normalize etme (1000 kutunun ortalaması).</summary>
    [Benchmark(OperationsPerInvoke = BoxCount)]
    public float ToNormalized()
    {
        var sum = 0f;
        for (var i = 0; i < _boxes.Length; i += 4)
        {
            sum += _geometry.ToNormalized(_boxes[i], _boxes[i + 1], _boxes[i + 2], _boxes[i + 3]).Area;
        }

        return sum;
    }

    /// <summary>Geometri nesnesinin oluşturulması ve ölçeğin hesaplanması (kamera/çözünürlük değiştiğinde).</summary>
    [Benchmark]
    public int CreateAndMeasure()
    {
        var geometry = new LetterboxGeometry(416, 416, 1280, 720);
        return geometry.ContentWidth + geometry.ContentHeight;
    }
}
