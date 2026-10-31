using Gozcu.Contracts.Analysis;
using Gozcu.Vision;

namespace Gozcu.AnalysisService.Snapshots;

/// <summary>
/// Analiz edilen karenin hata ayıklama görüntüsü: letterbox dolgusu kırpılır (yalnızca görüntü alanı kalır) ve tespit
/// kutuları 2 piksel kalınlığında çizilir (insan yeşil, araç turuncu).
/// </summary>
public static class SnapshotRenderer
{
    public const int LineThickness = 2;

    // BGR sırasıyla.
    internal static readonly (byte B, byte G, byte R) PersonColor = (0x50, 0xB9, 0x3F);   // #3fb950
    internal static readonly (byte B, byte G, byte R) VehicleColor = (0x3E, 0x88, 0xF0);  // #f0883e

    /// <summary>Model boyutundaki BGR24 kareden, kutuları çizilmiş içerik alanını BMP olarak üretir.</summary>
    public static byte[] RenderBmp(ReadOnlySpan<byte> modelFrame, LetterboxGeometry geometry, IReadOnlyList<Detection> detections)
    {
        var (pixels, width, height) = Render(modelFrame, geometry, detections);
        return BmpWriter.Encode(pixels, width, height);
    }

    /// <summary>Kırpılmış ve kutuları çizilmiş BGR24 pikselleri (üstten alta) döner.</summary>
    internal static (byte[] Pixels, int Width, int Height) Render(
        ReadOnlySpan<byte> modelFrame, LetterboxGeometry geometry, IReadOnlyList<Detection> detections)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(detections);
        var width = Math.Clamp(geometry.ContentWidth, 1, geometry.ModelWidth);
        var height = Math.Clamp(geometry.ContentHeight, 1, geometry.ModelHeight);
        var modelStride = geometry.ModelWidth * 3;
        ArgumentOutOfRangeException.ThrowIfLessThan(modelFrame.Length, modelStride * geometry.ModelHeight, nameof(modelFrame));

        // Görüntü sol üst köşededir; her satırın ilk "width" pikseli içeriktir.
        var stride = width * 3;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            modelFrame.Slice(y * modelStride, stride).CopyTo(pixels.AsSpan(y * stride, stride));
        }

        foreach (var detection in detections)
        {
            // Kutular kaynağa göre normalizedir; içerik alanı kaynağın ölçeklenmiş halidir.
            var box = detection.Box;
            var left = (int)Math.Round(box.X * width);
            var top = (int)Math.Round(box.Y * height);
            var right = (int)Math.Round((box.X + box.Width) * width) - 1;
            var bottom = (int)Math.Round((box.Y + box.Height) * height) - 1;
            var color = detection.Category == ObjectCategory.Person ? PersonColor : VehicleColor;
            DrawRectangle(pixels, width, height, left, top, right, bottom, color);
        }

        return (pixels, width, height);
    }

    private static void DrawRectangle(byte[] pixels, int width, int height, int left, int top, int right, int bottom, (byte B, byte G, byte R) color)
    {
        left = Math.Clamp(left, 0, width - 1);
        right = Math.Clamp(right, 0, width - 1);
        top = Math.Clamp(top, 0, height - 1);
        bottom = Math.Clamp(bottom, 0, height - 1);
        if (right < left || bottom < top)
        {
            return;
        }

        for (var t = 0; t < LineThickness; t++)
        {
            FillRow(pixels, width, Math.Min(top + t, bottom), left, right, color);
            FillRow(pixels, width, Math.Max(bottom - t, top), left, right, color);
            FillColumn(pixels, width, Math.Min(left + t, right), top, bottom, color);
            FillColumn(pixels, width, Math.Max(right - t, left), top, bottom, color);
        }
    }

    private static void FillRow(byte[] pixels, int width, int y, int x0, int x1, (byte B, byte G, byte R) color)
    {
        for (var x = x0; x <= x1; x++)
        {
            SetPixel(pixels, width, x, y, color);
        }
    }

    private static void FillColumn(byte[] pixels, int width, int x, int y0, int y1, (byte B, byte G, byte R) color)
    {
        for (var y = y0; y <= y1; y++)
        {
            SetPixel(pixels, width, x, y, color);
        }
    }

    private static void SetPixel(byte[] pixels, int width, int x, int y, (byte B, byte G, byte R) color)
    {
        var offset = ((y * width) + x) * 3;
        pixels[offset] = color.B;
        pixels[offset + 1] = color.G;
        pixels[offset + 2] = color.R;
    }
}
