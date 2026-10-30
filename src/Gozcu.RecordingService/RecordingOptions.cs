namespace Gozcu.RecordingService;

public sealed class RecordingOptions
{
    public const string SectionName = "Recording";

    public string FfmpegPath { get; set; } = "ffmpeg";

    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>Kayıtların yazılacağı kök dizin; her kamera için bir alt dizin açılır.</summary>
    public string RootPath { get; set; } = "recordings";

    /// <summary>Kayıt parçası süresi (saniye). Parçalar saat sınırlarına hizalanır.</summary>
    public int SegmentSeconds { get; set; } = 60;

    public TimeSpan SocketTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan ReconnectInitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    public RetentionOptions Retention { get; set; } = new();

    public PlaybackOptions Playback { get; set; } = new();

    public TimeSpan SegmentDuration => TimeSpan.FromSeconds(SegmentSeconds);

    public string CameraDirectory(string cameraId) => Path.Combine(Path.GetFullPath(RootPath), cameraId);
}

public sealed class RetentionOptions
{
    /// <summary>Kayıtların toplam üst sınırı (bayt). 0 ise sınır yok.</summary>
    public long MaxTotalBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>Diskte her zaman boş kalması gereken alan (bayt).</summary>
    public long MinFreeBytes { get; set; } = 1L * 1024 * 1024 * 1024;

    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class PlaybackOptions
{
    /// <summary>Tek seferde dışa aktarılabilecek en uzun klip.</summary>
    public TimeSpan MaxExportDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Geri oynatma sayfasındaki "Canlı izleme" bağlantısı (akış servisinin tarayıcıdan erişilen adresi).</summary>
    public Uri PublicLiveUrl { get; set; } = new("http://localhost:5080/");
}
