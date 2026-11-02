using BenchmarkDotNet.Attributes;
using Gozcu.Contracts.Analysis;
using Gozcu.Vision;

namespace Gozcu.Benchmarks.Vision;

/// <summary>
/// <see cref="NonMaxSuppression.Apply"/>: aynı nesne için üretilmiş, çok örtüşen aday kutulardan en güvenlisini
/// bırakma. Algoritma açgözlüdür: adaylar güvene göre sıralanır (O(n log n)), her aday tutulan kutularla
/// karşılaştırılır (en kötü O(n·k)). Kod çözücüden gelen aday sayısı sahneye ve eşiğe göre birkaç düzineden
/// binlere çıkabilir; bu ölçüm 50/200/1000 adayda maliyetin nasıl büyüdüğünü gösterir. Adaylar gerçekçi olsun
/// diye her 10 aday bir nesnenin çevresinde kümelenir (yarısı insan, yarısı otomobil); böylece bastırma gerçekten
/// çalışır ve tutulan kutu sayısı aday sayısının yaklaşık onda biridir.
/// </summary>
[MemoryDiagnoser]
public class NonMaxSuppressionBenchmarks
{
    private const float IouThreshold = 0.45f;
    private const int MaxDetections = 100;

    private List<Detection> _candidates = [];

    /// <summary>NMS'e giren aday kutu sayısı.</summary>
    [Params(50, 200, 1000)]
    public int Boxes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(42);
        var objects = Math.Max(1, Boxes / 10);
        var centers = Enumerable.Range(0, objects)
            .Select(_ => (X: random.NextSingle() * 0.8f, Y: random.NextSingle() * 0.8f))
            .ToArray();

        _candidates = new List<Detection>(Boxes);
        for (var i = 0; i < Boxes; i++)
        {
            var objectIndex = i % objects;
            var (x, y) = centers[objectIndex];
            var jitter = 0.01f;
            var box = new BoundingBox(
                x + ((random.NextSingle() - 0.5f) * jitter),
                y + ((random.NextSingle() - 0.5f) * jitter),
                0.1f + (random.NextSingle() * jitter),
                0.15f + (random.NextSingle() * jitter));
            var isPerson = objectIndex % 2 == 0;
            _candidates.Add(new Detection(
                isPerson ? "person" : "car",
                isPerson ? ObjectCategory.Person : ObjectCategory.Vehicle,
                0.35f + (random.NextSingle() * 0.64f),
                box));
        }
    }

    /// <summary>Tüm adaylara NMS uygulanması; dönen tespit sayısı geri verilir.</summary>
    [Benchmark]
    public int Apply() => NonMaxSuppression.Apply(_candidates, IouThreshold, MaxDetections).Count;
}
