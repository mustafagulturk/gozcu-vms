namespace Gozcu.Media.Probing;

/// <summary>Bir medya dosyasının oynatılabilir olup olmadığını inceler. Birim testlerde sahtesiyle değiştirilir.</summary>
public interface IMediaProbe
{
    /// <summary>
    /// Dosyayı inceler. Dosya okunamıyorsa (ör. moov atomu yok) <see cref="MediaProbeResult.Readable"/> false olan
    /// bir sonuç döner. İnceleme aracının kendisi çalıştırılamazsa ya da zaman aşımına uğrarsa istisna fırlatır;
    /// bu durumda dosya hakkında hüküm verilemez.
    /// </summary>
    Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken);
}
