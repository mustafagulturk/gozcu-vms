namespace Gozcu.Vision;

/// <summary>
/// ffmpeg'den gelen BGR24 kareyi (HWC: satır satır, piksel başına B,G,R baytı) modelin beklediği CHW float tensöre
/// çevirir. YOLOX 0.1.1rc0 dışa aktarımları BGR kanal sırası ve 0..255 ham değer bekler; normalizasyon yapılmaz.
/// </summary>
public static class FrameTensor
{
    /// <summary>Verilen boyuttaki bir karenin tensör uzunluğu (3 × genişlik × yükseklik).</summary>
    public static int Length(int width, int height) => 3 * width * height;

    /// <summary>Kareyi <paramref name="destination"/>'a [B düzlemi, G düzlemi, R düzlemi] olarak yazar.</summary>
    /// <exception cref="ArgumentException">Kare veya hedef uzunluğu boyutla uyuşmuyorsa.</exception>
    public static void FillChw(ReadOnlySpan<byte> bgrFrame, int width, int height, Span<float> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var length = Length(width, height);
        if (bgrFrame.Length != length)
        {
            throw new ArgumentException(
                $"BGR24 kare {width}x{height}x3 = {length} bayt olmalı; {bgrFrame.Length} bayt geldi.", nameof(bgrFrame));
        }

        if (destination.Length < length)
        {
            throw new ArgumentException($"Hedef tampon en az {length} eleman olmalı; {destination.Length} eleman var.", nameof(destination));
        }

        var pixels = width * height;
        var blue = destination[..pixels];
        var green = destination.Slice(pixels, pixels);
        var red = destination.Slice(2 * pixels, pixels);
        for (int i = 0, j = 0; i < pixels; i++, j += 3)
        {
            blue[i] = bgrFrame[j];
            green[i] = bgrFrame[j + 1];
            red[i] = bgrFrame[j + 2];
        }
    }
}
