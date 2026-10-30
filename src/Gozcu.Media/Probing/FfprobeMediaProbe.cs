using System.Globalization;
using System.Text;
using System.Text.Json;
using Gozcu.Media.Processes;

namespace Gozcu.Media.Probing;

/// <summary>
/// ffprobe ile süre ve video kodeğini okur. Aniden kesilen bir kayıtta MP4'ün sonundaki moov atomu
/// yazılmamış olur; ffprobe bu dosyayı "moov atom not found" hatasıyla reddeder.
/// </summary>
public sealed class FfprobeMediaProbe : IMediaProbe
{
    private const int MaxErrorLines = 4;

    private readonly IProcessRunner _runner;
    private readonly string _ffprobePath;
    private readonly TimeSpan _timeout;

    public FfprobeMediaProbe(IProcessRunner runner, string ffprobePath = "ffprobe", TimeSpan? timeout = null)
    {
        _runner = runner;
        _ffprobePath = ffprobePath;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public static IReadOnlyList<string> Arguments(string path) =>
        ["-v", "error", "-show_entries", "format=duration:stream=codec_type,codec_name", "-of", "json", path];

    public async Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var errors = new List<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        int exitCode;
        try
        {
            exitCode = await _runner.RunAsync(
                new ProcessSpec(_ffprobePath, Arguments(path)),
                line => output.AppendLine(line),
                line =>
                {
                    // ffprobe asıl nedeni ("moov atom not found") genelde ilk satırda, genel hatayı sonda yazar.
                    if (!string.IsNullOrWhiteSpace(line) && errors.Count < MaxErrorLines)
                    {
                        errors.Add(line.Trim());
                    }
                },
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Zaman aşımı dosyanın bozuk olduğunu göstermez (ör. disk yavaş); karar verilemez.
            throw new TimeoutException($"ffprobe {path} için {_timeout.TotalSeconds:0} sn içinde yanıt vermedi");
        }

        if (exitCode != 0)
        {
            return MediaProbeResult.Unreadable(errors.Count == 0 ? $"ffprobe {exitCode} koduyla çıktı" : string.Join(" | ", errors));
        }

        return Parse(output.ToString());
    }

    /// <summary>ffprobe'un "-of json" çıktısını ayrıştırır.</summary>
    internal static MediaProbeResult Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var duration = TimeSpan.Zero;
            if (root.TryGetProperty("format", out var format)
                && format.TryGetProperty("duration", out var durationElement)
                && durationElement.ValueKind == JsonValueKind.String
                && double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                && seconds > 0)
            {
                duration = TimeSpan.FromSeconds(seconds);
            }

            string? codec = null;
            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (stream.TryGetProperty("codec_type", out var type) && type.GetString() == "video")
                    {
                        codec = stream.TryGetProperty("codec_name", out var name) ? name.GetString() : "unknown";
                        break;
                    }
                }
            }

            var error = codec is null ? "video akışı yok" : duration <= TimeSpan.Zero ? "süre sıfır ya da bilinmiyor" : null;
            return new MediaProbeResult(true, duration, codec, error);
        }
        catch (JsonException ex)
        {
            return MediaProbeResult.Unreadable("ffprobe çıktısı okunamadı: " + ex.Message);
        }
    }
}
