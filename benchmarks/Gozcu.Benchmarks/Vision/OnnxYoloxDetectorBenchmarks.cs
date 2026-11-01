using BenchmarkDotNet.Attributes;
using Gozcu.Benchmarks.Support;
using Gozcu.Vision;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gozcu.Benchmarks.Vision;

/// <summary>
/// <see cref="OnnxYoloxDetector.Detect"/>: tek bir karede uçtan uca tespit — BGR24 → CHW tensör, ONNX Runtime
/// (CPU) ile <c>yolox_tiny</c> çıkarımı, kod çözme ve NMS. Analiz servisinin kare başına asıl maliyeti budur ve
/// bir makinenin aynı anda kaç kamerayı analiz edebileceğini belirler (ör. Mean 40 ms ise tek çekirdek saniyede
/// ~25 kare işler).
/// <para>
/// <see cref="IntraOpThreads"/> 1/2/4 karşılaştırması: tek kameranın gecikmesi iş parçacığı arttıkça düşer, ama
/// servis varsayılan olarak 1 kullanır çünkü birden çok kamera aynı oturumu paralel çağırır ve çekirdekler kameralar
/// arasında paylaşılır. Bu ölçüm, 2 ya da 4 iş parçacığının tek kare gecikmesini ne kadar iyileştirdiğini (ve buna
/// değip değmediğini) gösterir.
/// </para>
/// <para>
/// Model <c>GOZCU_MODEL_PATH</c> ortam değişkeninden ya da üst klasörlerdeki <c>models/yolox_tiny.onnx</c>'ten
/// alınır; bulunamazsa GlobalSetup açıklayıcı bir hata fırlatır ve yalnızca bu sınıfın ölçümleri başarısız olur.
/// Kare olarak ffmpeg varsa test görüntüsü (otopark: otomobil, insan, bisiklet), yoksa rastgele baytlar kullanılır.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class OnnxYoloxDetectorBenchmarks
{
    private OnnxYoloxDetector? _detector;
    private byte[] _frame = [];
    private LetterboxGeometry? _geometry;

    /// <summary>Tek çıkarımın kullandığı ONNX Runtime iş parçacığı sayısı.</summary>
    [Params(1, 2, 4)]
    public int IntraOpThreads { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var modelPath = RepoFiles.ResolveModelPath();
        _detector = new OnnxYoloxDetector(
            new YoloxDetectorOptions { ModelPath = modelPath, IntraOpThreads = IntraOpThreads },
            NullLogger.Instance);

        var model = _detector.Model;
        var testImage = BenchmarkFrames.TryLoadTestImage(model.InputWidth, model.InputHeight);
        _frame = testImage ?? BenchmarkFrames.Synthetic(model.InputWidth, model.InputHeight);
        _geometry = testImage is null
            ? new LetterboxGeometry(model.InputWidth, model.InputHeight, model.InputWidth, model.InputHeight)
            : new LetterboxGeometry(model.InputWidth, model.InputHeight, BenchmarkFrames.TestImageWidth, BenchmarkFrames.TestImageHeight);

        Console.WriteLine(
            $"// Model: {modelPath} ({model.InputWidth}x{model.InputHeight}), kare: {(testImage is null ? "sentetik" : "test görüntüsü")}, " +
            $"tespit: {_detector.Detect(_frame, _geometry).Count}");
    }

    [GlobalCleanup]
    public void Cleanup() => _detector?.Dispose();

    /// <summary>Tek karede uçtan uca tespit.</summary>
    [Benchmark]
    public int Detect() => _detector!.Detect(_frame, _geometry!).Count;
}
