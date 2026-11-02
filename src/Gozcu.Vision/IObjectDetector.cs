namespace Gozcu.Vision;

/// <summary>Model ve girişi hakkında bilgi.</summary>
/// <param name="Name">Model dosyasının adı (ör. yolox_tiny).</param>
/// <param name="InputWidth">Modelin beklediği giriş genişliği (piksel).</param>
/// <param name="InputHeight">Modelin beklediği giriş yüksekliği (piksel).</param>
public sealed record DetectorModelInfo(string Name, int InputWidth, int InputHeight);

/// <summary>Nesne tespit edici. Birim testlerde sahtesiyle değiştirilir. Gerçekleştirimler iş parçacığı güvenlidir.</summary>
public interface IObjectDetector
{
    DetectorModelInfo Model { get; }

    /// <summary>
    /// Model boyutunda, letterbox uygulanmış, BGR24 (satır satır, piksel başına 3 bayt) bir karede nesne arar.
    /// </summary>
    /// <param name="bgrFrame">Uzunluğu InputWidth × InputHeight × 3 olan kare.</param>
    /// <param name="geometry">Kutuları kaynak görüntüye geri eşlemek için letterbox geometrisi.</param>
    /// <returns>Güven eşiğini geçen, NMS uygulanmış, güvene göre azalan sıralı tespitler.</returns>
    IReadOnlyList<Detection> Detect(ReadOnlySpan<byte> bgrFrame, LetterboxGeometry geometry);
}
