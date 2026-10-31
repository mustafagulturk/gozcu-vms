using Gozcu.Contracts.Analysis;

namespace Gozcu.Vision;

/// <summary>
/// Kaynak görüntüye göre 0..1 aralığında normalize edilmiş eksen hizalı kutu (sol üst köşe + boyut).
/// Normalize koordinatlar çözünürlükten bağımsızdır; web panel kutuyu hangi boyutta çizerse çizsin doğru yere düşer.
/// </summary>
public readonly record struct BoundingBox(float X, float Y, float Width, float Height)
{
    public float Area => Width * Height;

    /// <summary>Kesişim / birleşim oranı (Intersection over Union).</summary>
    public float IntersectionOverUnion(BoundingBox other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(X + Width, other.X + other.Width);
        var bottom = Math.Min(Y + Height, other.Y + other.Height);
        var intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        var union = Area + other.Area - intersection;
        return union <= 0 ? 0 : intersection / union;
    }
}

/// <summary>Tek bir tespit: COCO etiketi, eşlendiği kategori, güven skoru ve normalize kutu.</summary>
public sealed record Detection(string Label, ObjectCategory Category, float Confidence, BoundingBox Box);
