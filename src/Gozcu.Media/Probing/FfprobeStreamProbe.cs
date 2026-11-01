using System.Globalization;
using System.Text;
using System.Text.Json;
using Gozcu.Media.Processes;

namespace Gozcu.Media.Probing;

/// <summary>Canlı bir akışın (RTSP) bağlantı testi sonucu.</summary>
/// <param name="Ok">Bağlanılabildi ve bir video akışı bulundu.</param>
/// <param name="Error">Başarısızsa nedeni (ffprobe'un hata satırları ya da zaman aşımı).</param>
public sealed record StreamProbeResult(bool Ok, string? Codec, int? Width, int? Height, double? FramesPerSecond, string? Error)
{
    public static StreamProbeResult Failed(string error) => new(false, null, null, null, null, error);
}

/// <summary>Canlı akışa bağlanıp ilk video akışının özelliklerini okur. Birim testlerde sahtesiyle değiştirilir.</summary>
public interface IStreamProbe
{
    /// <summary>Akışı dener. Bağlantı hataları ve zaman aşımı istisna değil, başarısız sonuç olarak döner.</summary>
    Task<StreamProbeResult> ProbeAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>
/// ffprobe ile RTSP bağlantı testi: bağlanır, ilk video akışının kodeğini, çözünürlüğünü ve kare hızını okur.
/// Kamera eklemeden önce adresin ve parolanın doğru olduğunu görmek için kullanılır.
/// </summary>
public sealed class FfprobeStreamProbe : IStreamProbe
{
    private const int MaxErrorLines = 4;

    private readonly IProcessRunner _runner;
    private readonly string _ffprobePath;
    private readonly TimeSpan _timeout;

    public FfprobeStreamProbe(IProcessRunner runner, string ffprobePath = "ffprobe", TimeSpan? timeout = null)
    {
        _runner = runner;
        _ffprobePath = ffprobePath;
        _timeout = timeout ?? TimeSpan.FromSeconds(8);
    }

    public TimeSpan Timeout => _timeout;

    public static IReadOnlyList<string> Arguments(Uri url, TimeSpan socketTimeout)
    {
        ArgumentNullException.ThrowIfNull(url);
        return
        [
            "-v", "error",
            "-rtsp_transport", "tcp",
            "-timeout", ((long)socketTimeout.TotalMicroseconds).ToString(CultureInfo.InvariantCulture),
            "-select_streams", "v:0",
            "-show_entries", "stream=codec_name,width,height,avg_frame_rate,r_frame_rate",
            "-of", "json",
            url.ToString(),
        ];
    }

    public async Task<StreamProbeResult> ProbeAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        var output = new StringBuilder();
        var errors = new List<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        int exitCode;
        try
        {
            // Soket zaman aşımı toplam süreden biraz kısa tutulur ki ffprobe kendi hatasını yazabilsin.
            var socketTimeout = _timeout > TimeSpan.FromSeconds(2) ? _timeout - TimeSpan.FromSeconds(1) : _timeout;
            exitCode = await _runner.RunAsync(
                new ProcessSpec(_ffprobePath, Arguments(url, socketTimeout)),
                line => output.AppendLine(line),
                line =>
                {
                    if (!string.IsNullOrWhiteSpace(line) && errors.Count < MaxErrorLines)
                    {
                        errors.Add(line.Trim());
                    }
                },
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StreamProbeResult.Failed($"Kamera {_timeout.TotalSeconds:0} sn içinde yanıt vermedi.");
        }

        if (exitCode != 0)
        {
            return StreamProbeResult.Failed(errors.Count == 0 ? $"ffprobe {exitCode} koduyla çıktı" : string.Join(" | ", errors));
        }

        return Parse(output.ToString());
    }

    /// <summary>ffprobe'un "-of json" çıktısını ayrıştırır.</summary>
    internal static StreamProbeResult Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("streams", out var streams)
                || streams.ValueKind != JsonValueKind.Array
                || streams.GetArrayLength() == 0)
            {
                return StreamProbeResult.Failed("Akışta video bulunamadı.");
            }

            var stream = streams[0];
            var codec = stream.TryGetProperty("codec_name", out var name) ? name.GetString() : null;
            int? width = stream.TryGetProperty("width", out var w) && w.TryGetInt32(out var wv) ? wv : null;
            int? height = stream.TryGetProperty("height", out var h) && h.TryGetInt32(out var hv) ? hv : null;
            var fps = ParseRate(stream, "avg_frame_rate") ?? ParseRate(stream, "r_frame_rate");
            return new StreamProbeResult(true, codec ?? "unknown", width, height, fps, null);
        }
        catch (JsonException ex)
        {
            return StreamProbeResult.Failed("ffprobe çıktısı okunamadı: " + ex.Message);
        }
    }

    /// <summary>"25/1" ya da "30000/1001" biçimindeki oranı okur; "0/0" bilinmiyor demektir.</summary>
    private static double? ParseRate(JsonElement stream, string property)
    {
        if (!stream.TryGetProperty(property, out var value) || value.GetString() is not { } text)
        {
            return null;
        }

        var parts = text.Split('/');
        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            || numerator <= 0 || denominator <= 0)
        {
            return null;
        }

        return Math.Round(numerator / denominator, 2);
    }
}
