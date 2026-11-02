using BenchmarkDotNet.Attributes;
using Gozcu.Media.Ffmpeg;

namespace Gozcu.Benchmarks.Media;

/// <summary>
/// <see cref="FfmpegProgressParser.TryAccept"/>: ffmpeg'in <c>-progress pipe:1</c> çıktısındaki "anahtar=değer"
/// satırlarını ayrıştırma. Her kayıt ve akış süreci saniyede bir 12 satırlık bir rapor bloğu yazar ve süreç
/// gözetmeni akışın canlı olup olmadığına bu raporlarla karar verir. Kamera sayısı arttıkça bu satırlar sürekli
/// ayrıştırıldığından, satır başına alt dize (<c>Substring</c>/<c>Trim</c>) ayırmalarının toplamı önemlidir.
/// 100 blok (1200 satır) ayrıştırılır ve <c>OperationsPerInvoke</c> ile Mean ve Allocated blok başına verilir.
/// </summary>
[MemoryDiagnoser]
public class FfmpegProgressParserBenchmarks
{
    private const int BlockCount = 100;

    private string[] _lines = [];

    [GlobalSetup]
    public void Setup()
    {
        var lines = new List<string>(BlockCount * 12);
        for (var i = 0; i < BlockCount; i++)
        {
            var outTimeUs = (i + 1) * 1_000_000L;
            lines.Add($"frame={(i + 1) * 25}");
            lines.Add("fps=25.00");
            lines.Add("stream_0_0_q=-1.0");
            lines.Add("bitrate=2048.3kbits/s");
            lines.Add($"total_size={(i + 1) * 256_000}");
            lines.Add($"out_time_us={outTimeUs}");
            lines.Add($"out_time_ms={outTimeUs}");
            lines.Add($"out_time=00:00:{i % 60:00}.000000");
            lines.Add("dup_frames=0");
            lines.Add("drop_frames=0");
            lines.Add("speed=1.00x");
            lines.Add(i == BlockCount - 1 ? "progress=end" : "progress=continue");
        }

        _lines = [.. lines];
    }

    /// <summary>Bir rapor bloğunun (12 satır) ayrıştırılması.</summary>
    [Benchmark(OperationsPerInvoke = BlockCount)]
    public int ParseBlocks()
    {
        var parser = new FfmpegProgressParser();
        var reports = 0;
        foreach (var line in _lines)
        {
            if (parser.TryAccept(line, out _))
            {
                reports++;
            }
        }

        return reports;
    }
}
