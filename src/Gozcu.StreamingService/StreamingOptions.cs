namespace Gozcu.StreamingService;

public sealed class StreamingOptions
{
    public const string SectionName = "Streaming";

    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Tarayıcının canlı HLS akışını çekeceği MediaMTX adresi (ör. http://localhost:8888).</summary>
    public Uri PublicHlsBaseUrl { get; set; } = new("http://localhost:8888");

    /// <summary>Kayıt servisinin geri oynatma sayfası (ör. http://localhost:5081/). Boşsa bağlantı gösterilmez.</summary>
    public Uri? PublicPlaybackUrl { get; set; } = new("http://localhost:5081/");

    /// <summary>Bu süre boyunca yeni medya gelmezse kamera Degraded sayılır.</summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>Alınan medya süresinin duvar saatine oranı bu eşiğin altına düşerse kamera Degraded sayılır.</summary>
    public double DegradedRateThreshold { get; set; } = 0.8;

    public TimeSpan SocketTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan ReconnectInitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);
}
