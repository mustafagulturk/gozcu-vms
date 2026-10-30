namespace Gozcu.Media.Probing;

/// <summary>Bir medya dosyasının inceleme sonucu.</summary>
/// <param name="Readable">Kapsayıcı okunabildi mi (ffprobe başarıyla çıktı mı).</param>
/// <param name="Duration">Kapsayıcıda yazan süre; bilinmiyorsa sıfır.</param>
/// <param name="VideoCodec">İlk video akışının kodeği; video akışı yoksa null.</param>
/// <param name="Error">Okunamadıysa nedeni.</param>
public sealed record MediaProbeResult(bool Readable, TimeSpan Duration, string? VideoCodec, string? Error = null)
{
    /// <summary>Kayıt parçası olarak oynatılabilir: okunabilir, bir video akışı var ve süresi sıfırdan büyük.</summary>
    public bool IsPlayable => Readable && VideoCodec is not null && Duration > TimeSpan.Zero;

    public static MediaProbeResult Unreadable(string error) => new(false, TimeSpan.Zero, null, error);

    public static MediaProbeResult Playable(TimeSpan duration, string videoCodec) => new(true, duration, videoCodec);
}
