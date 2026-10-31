using System.Globalization;

namespace Gozcu.Media.Ffmpeg;

/// <summary>
/// Servislerin kullandığı ffmpeg komut satırlarını üretir. İzleme ve kayıt komutları yeniden kodlama yapmaz (-c copy);
/// videoyu yalnızca analiz komutu (<see cref="AnalysisFrames"/>) çözer.
/// </summary>
public static class FfmpegCommands
{
    /// <summary>RTSP soket zaman aşımı; bu süre veri gelmezse ffmpeg çıkar ve yeniden bağlanılır.</summary>
    public static readonly TimeSpan DefaultSocketTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Akışı çözmeden okuyup null çıkışa yazan izleme komutu. Sağlık bilgisi
    /// stdout'a yazılan -progress satırlarından, akış bilgisi stderr'den okunur.
    /// </summary>
    public static IReadOnlyList<string> Monitor(Uri rtspUrl, TimeSpan? socketTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(rtspUrl);
        return
        [
            "-hide_banner",
            "-nostats",
            "-loglevel", "info",
            .. RtspInput(rtspUrl, socketTimeout),
            "-map", "0:v:0",
            "-c", "copy",
            "-f", "null",
            "-progress", "pipe:1",
            "-",
        ];
    }

    /// <summary>
    /// Akışı yeniden kodlamadan sabit süreli MP4 parçalarına bölen kayıt komutu.
    /// Parçalar saat sınırlarına hizalanır (ör. 60 sn için her dakikanın başı) ve
    /// biten her parça stdout'a CSV satırı olarak yazılır: "dosya,başlangıç,bitiş".
    /// </summary>
    public static IReadOnlyList<string> SegmentRecorder(
        Uri rtspUrl,
        string outputDirectory,
        string cameraId,
        TimeSpan segmentDuration,
        TimeSpan? socketTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(rtspUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraId);
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentDuration, TimeSpan.FromSeconds(1));

        return
        [
            "-hide_banner",
            "-nostats",
            "-loglevel", "warning",
            .. RtspInput(rtspUrl, socketTimeout),
            "-map", "0:v:0",
            "-c", "copy",
            "-f", "segment",
            "-segment_time", segmentDuration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-segment_atclocktime", "1",
            "-reset_timestamps", "1",
            "-strftime", "1",
            "-segment_format", "mp4",
            "-segment_list", "pipe:1",
            "-segment_list_type", "csv",
            Path.Combine(outputDirectory, SegmentFileName.Pattern(cameraId)),
        ];
    }

    /// <summary>
    /// Analiz servisinin kare komutu. Sistemde videoyu çözen (decode) TEK komut budur; akış ve kayıt servisleri
    /// yeniden kodlamadan kopyalar. Akış saniyede <paramref name="framesPerSecond"/> kareye seyreltilir, en-boy oranı
    /// korunarak <paramref name="width"/>×<paramref name="height"/> içine sol üst köşeye ölçeklenir, kalan alan gri (114)
    /// ile doldurulur (letterbox) ve kareler başlıksız BGR24 olarak stdout'a yazılır: her kare tam olarak W×H×3 bayttır.
    /// Giriş akışının çözünürlüğü stderr'deki ilk "Stream #0:0: Video: ..." satırından okunur (loglevel info);
    /// sonraki "Video: rawvideo" satırı çıkış akışıdır.
    /// </summary>
    public static IReadOnlyList<string> AnalysisFrames(
        Uri rtspUrl,
        double framesPerSecond,
        int width,
        int height,
        TimeSpan? socketTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(rtspUrl);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerSecond);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(framesPerSecond, 60);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 16);

        var fps = framesPerSecond.ToString("0.###", CultureInfo.InvariantCulture);
        var w = width.ToString(CultureInfo.InvariantCulture);
        var h = height.ToString(CultureInfo.InvariantCulture);
        return
        [
            "-hide_banner",
            "-nostats",
            "-nostdin",
            "-loglevel", "info",
            .. RtspInput(rtspUrl, socketTimeout),
            "-map", "0:v:0",
            "-an",
            "-vf", $"fps={fps},scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:0:0:color=0x727272",
            "-pix_fmt", "bgr24",
            "-f", "rawvideo",
            "pipe:1",
        ];
    }

    /// <summary>ffmpeg'in parça dosya adlarındaki strftime zamanını UTC üretmesi için ortam değişkenleri.</summary>
    public static IReadOnlyDictionary<string, string> UtcEnvironment { get; } =
        new Dictionary<string, string> { ["TZ"] = "UTC" };

    private static string[] RtspInput(Uri rtspUrl, TimeSpan? socketTimeout)
    {
        var timeoutMicroseconds = (long)(socketTimeout ?? DefaultSocketTimeout).TotalMicroseconds;
        return
        [
            "-rtsp_transport", "tcp",
            "-timeout", timeoutMicroseconds.ToString(CultureInfo.InvariantCulture),
            "-i", rtspUrl.ToString(),
        ];
    }
}
