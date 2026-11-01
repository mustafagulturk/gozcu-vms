using BenchmarkDotNet.Attributes;
using Gozcu.Benchmarks.Support;
using Gozcu.Media.Frames;

namespace Gozcu.Benchmarks.Media;

/// <summary>
/// <see cref="RawFrameReader.ReadAsync"/>: ffmpeg'in <c>-f rawvideo pipe:1</c> çıktısından 416x416 BGR24
/// (519.168 bayt) kareleri okuma. Analiz servisi her kamera için bu döngüde durur; boru tek okumada kareden az
/// bayt verdiğinde okuyucu kareyi birden çok <c>ReadAsync</c> çağrısıyla tamamlar. <see cref="ChunkSize"/>:
/// 65536 Linux boru tamponunu (her kare ~8 okumada tamamlanır), 0 ise tek okumada tam kare veren ideal akışı
/// temsil eder; aradaki fark parça parça okumanın ek maliyetidir. 30 kare okunur ve <c>OperationsPerInvoke</c>
/// ile Mean kare başına verilir. Tampon yeniden kullanıldığından Allocated 0 B'ye yakın olmalıdır.
/// </summary>
[MemoryDiagnoser]
public class RawFrameReaderBenchmarks
{
    private const int FrameSize = 416 * 416 * 3;
    private const int FrameCount = 30;

    private readonly byte[] _buffer = new byte[FrameSize];
    private ChunkedReadStream? _stream;
    private RawFrameReader? _reader;

    /// <summary>Akışın tek okumada verdiği en fazla bayt; 0 = sınırsız (tam kare).</summary>
    [Params(0, 65536)]
    public int ChunkSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var data = new byte[FrameSize * FrameCount];
        new Random(42).NextBytes(data);
        _stream = new ChunkedReadStream(data, ChunkSize == 0 ? int.MaxValue : ChunkSize);
        _reader = new RawFrameReader(_stream, FrameSize);
    }

    [GlobalCleanup]
    public void Cleanup() => _stream?.Dispose();

    /// <summary>Akıştaki tüm karelerin sırayla okunması (kare başına süre).</summary>
    [Benchmark(OperationsPerInvoke = FrameCount)]
    public async Task<int> ReadFrames()
    {
        _stream!.Rewind();
        var frames = 0;
        while (await _reader!.ReadAsync(_buffer, CancellationToken.None))
        {
            frames++;
        }

        return frames;
    }
}
