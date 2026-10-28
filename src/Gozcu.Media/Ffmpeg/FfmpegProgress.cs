namespace Gozcu.Media.Ffmpeg;

/// <summary>ffmpeg -progress çıktısındaki bir rapor bloğu.</summary>
/// <param name="OutTime">Çıkışa yazılan medya zamanı; artıyorsa akış geliyor demektir.</param>
/// <param name="Speed">Gerçek zamana oranla işleme hızı (canlı akışta ~1.0).</param>
/// <param name="IsFinal">ffmpeg son raporu verdi ("progress=end").</param>
public sealed record FfmpegProgress(TimeSpan? OutTime, double? Speed, bool IsFinal);
