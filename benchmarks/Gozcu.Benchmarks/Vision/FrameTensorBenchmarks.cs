using BenchmarkDotNet.Attributes;
using Gozcu.Benchmarks.Support;
using Gozcu.Vision;

namespace Gozcu.Benchmarks.Vision;

/// <summary>
/// <see cref="FrameTensor.FillChw"/>: ffmpeg'den gelen BGR24 (HWC, bayt) kareyi modelin beklediği CHW float
/// tensöre çevirme. Analiz servisinde her kamera için saniyede birkaç kez, ONNX çıkarımından hemen önce çalışır;
/// 416x416 karede 519.168 bayt okunup 2 MB float yazılır. Bu döngü çıkarım süresinin yanında küçük kalmalı ve
/// hiç bellek ayırmamalıdır (tampon <c>ArrayPool</c>'dan gelir) — Allocated sütunu 0 B olmalıdır.
/// 640 boyutu, YOLOX-S gibi daha büyük modellere geçişin ön işleme maliyetini göstermek için eklenmiştir.
/// </summary>
[MemoryDiagnoser]
public class FrameTensorBenchmarks
{
    private byte[] _frame = [];
    private float[] _tensor = [];

    /// <summary>Model giriş kenarı (kare giriş): yolox_tiny/nano için 416, yolox_s ve üstü için 640.</summary>
    [Params(416, 640)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _frame = BenchmarkFrames.Synthetic(Size, Size);
        _tensor = new float[FrameTensor.Length(Size, Size)];
    }

    /// <summary>Tek karenin BGR24 → CHW float dönüşümü.</summary>
    [Benchmark]
    public float FillChw()
    {
        FrameTensor.FillChw(_frame, Size, Size, _tensor);
        return _tensor[^1];
    }
}
