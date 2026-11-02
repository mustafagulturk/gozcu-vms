using System.Buffers.Binary;

namespace Gozcu.AnalysisService.Snapshots;

/// <summary>
/// Sıkıştırılmamış 24 bit BMP kodlayıcı. Tarayıcılar BMP'yi doğrudan gösterir; böylece hata ayıklama sayfası için
/// görüntü kütüphanesine gerek kalmaz. BMP satırları alttan üste saklanır ve her satır 4 baytın katına doldurulur.
/// </summary>
public static class BmpWriter
{
    public const int FileHeaderSize = 14;
    public const int InfoHeaderSize = 40;
    public const int HeaderSize = FileHeaderSize + InfoHeaderSize;

    /// <summary>72 DPI ≈ 2835 piksel/metre.</summary>
    private const int PixelsPerMeter = 2835;

    /// <summary>Bir BMP satırının dolgu dahil bayt uzunluğu.</summary>
    public static int RowStride(int width) => ((width * 3) + 3) & ~3;

    /// <summary>Üstten alta, satır satır BGR24 pikselleri BMP dosyasına kodlar.</summary>
    /// <param name="bgr">Uzunluğu en az <paramref name="width"/> × <paramref name="height"/> × 3 olan, dolgusuz pikseller.</param>
    public static byte[] Encode(ReadOnlySpan<byte> bgr, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var rowBytes = width * 3;
        ArgumentOutOfRangeException.ThrowIfLessThan(bgr.Length, rowBytes * height, nameof(bgr));

        var stride = RowStride(width);
        var imageSize = stride * height;
        var bmp = new byte[HeaderSize + imageSize];
        var span = bmp.AsSpan();

        // BITMAPFILEHEADER
        span[0] = (byte)'B';
        span[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(span[2..], bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[6..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(span[10..], HeaderSize);

        // BITMAPINFOHEADER (pozitif yükseklik: satırlar alttan üste)
        BinaryPrimitives.WriteInt32LittleEndian(span[14..], InfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(span[18..], width);
        BinaryPrimitives.WriteInt32LittleEndian(span[22..], height);
        BinaryPrimitives.WriteInt16LittleEndian(span[26..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[28..], 24);
        BinaryPrimitives.WriteInt32LittleEndian(span[30..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(span[34..], imageSize);
        BinaryPrimitives.WriteInt32LittleEndian(span[38..], PixelsPerMeter);
        BinaryPrimitives.WriteInt32LittleEndian(span[42..], PixelsPerMeter);
        BinaryPrimitives.WriteInt32LittleEndian(span[46..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(span[50..], 0);

        for (var y = 0; y < height; y++)
        {
            var source = bgr.Slice(y * rowBytes, rowBytes);
            var target = span.Slice(HeaderSize + ((height - 1 - y) * stride), rowBytes);
            source.CopyTo(target);
        }

        return bmp;
    }
}
