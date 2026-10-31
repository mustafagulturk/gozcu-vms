namespace Gozcu.Vision;

/// <summary>
/// Kaynak kareyi en-boy oranını bozmadan model girişine sığdırma (letterbox) geometrisi. Görüntü sol üst köşeye
/// ölçeklenir, kalan alan gri (114) ile doldurulur. ffmpeg'in
/// <c>scale=W:H:force_original_aspect_ratio=decrease,pad=W:H:0:0</c> süzgeci aynı yerleşimi üretir.
/// </summary>
public sealed record LetterboxGeometry(int ModelWidth, int ModelHeight, int SourceWidth, int SourceHeight)
{
    /// <summary>Kaynağı modele sığdıran ölçek: min(Wm/Ws, Hm/Hs).</summary>
    public double Scale => Math.Min((double)ModelWidth / SourceWidth, (double)ModelHeight / SourceHeight);

    /// <summary>Model girişinde görüntünün kapladığı genişlik (piksel); geri kalanı dolgu.</summary>
    public int ContentWidth => Math.Min(ModelWidth, (int)Math.Round(SourceWidth * Scale));

    public int ContentHeight => Math.Min(ModelHeight, (int)Math.Round(SourceHeight * Scale));

    /// <summary>Model pikselleriyle verilen kutuyu (sol, üst, genişlik, yükseklik) kaynağa göre normalize eder ve [0,1]'e kırpar.</summary>
    public BoundingBox ToNormalized(float left, float top, float width, float height)
    {
        var x1 = Math.Clamp(left / ContentWidth, 0f, 1f);
        var y1 = Math.Clamp(top / ContentHeight, 0f, 1f);
        var x2 = Math.Clamp((left + width) / ContentWidth, 0f, 1f);
        var y2 = Math.Clamp((top + height) / ContentHeight, 0f, 1f);
        return new BoundingBox(x1, y1, x2 - x1, y2 - y1);
    }
}
