using System.Buffers.Binary;
using Gozcu.AnalysisService.Snapshots;
using Gozcu.Contracts.Analysis;
using Gozcu.Vision;

namespace Gozcu.UnitTests.Analysis;

public class BmpWriterTests
{
    [Theory]
    [InlineData(1, 4)]
    [InlineData(2, 8)]
    [InlineData(3, 12)]
    [InlineData(4, 12)]
    [InlineData(5, 16)]
    [InlineData(416, 1248)]
    public void Rows_are_padded_to_four_bytes(int width, int expectedStride)
    {
        Assert.Equal(expectedStride, BmpWriter.RowStride(width));
    }

    [Fact]
    public void Header_fields_describe_uncompressed_bottom_up_24_bit_image()
    {
        var bmp = BmpWriter.Encode(new byte[3 * 2 * 3], width: 3, height: 2);
        var span = bmp.AsSpan();

        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal(54 + (12 * 2), bmp.Length);
        Assert.Equal(bmp.Length, BinaryPrimitives.ReadInt32LittleEndian(span[2..]));
        Assert.Equal(54, BinaryPrimitives.ReadInt32LittleEndian(span[10..]));
        Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(span[14..]));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(span[18..]));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(span[22..]));   // pozitif: alttan üste
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(span[26..]));
        Assert.Equal(24, BinaryPrimitives.ReadInt16LittleEndian(span[28..]));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(span[30..]));   // BI_RGB
        Assert.Equal(24, BinaryPrimitives.ReadInt32LittleEndian(span[34..]));
    }

    [Fact]
    public void Pixel_rows_are_stored_bottom_up_with_zero_padding()
    {
        // 1×2 görüntü: üst satır (1,2,3), alt satır (4,5,6). Satır 3 bayt + 1 bayt dolgu.
        var bmp = BmpWriter.Encode([1, 2, 3, 4, 5, 6], width: 1, height: 2);

        Assert.Equal(new byte[] { 4, 5, 6, 0, 1, 2, 3, 0 }, bmp[54..]);
    }

    [Fact]
    public void Too_short_pixel_buffer_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BmpWriter.Encode(new byte[5], 1, 2));
    }

    [Fact]
    public void Snapshot_is_cropped_to_content_area_and_boxes_are_drawn()
    {
        // 16:9 kaynak 32×32 modelde sol üstte 32×18 alan kaplar; geri kalan gri dolgudur.
        var geometry = new LetterboxGeometry(32, 32, 1280, 720);
        var frame = Enumerable.Repeat((byte)114, 32 * 32 * 3).ToArray();
        var detections = new List<Detection>
        {
            new("person", ObjectCategory.Person, 0.9f, new BoundingBox(0.25f, 0.5f, 0.5f, 0.5f)),
        };

        var (pixels, width, height) = SnapshotRenderer.Render(frame, geometry, detections);

        Assert.Equal((32, 18), (width, height));
        Assert.Equal(32 * 18 * 3, pixels.Length);

        // Kutu: x 8..23, y 9..17. Sol üst köşe ve 2 piksel kalınlık.
        Assert.Equal(SnapshotRenderer.PersonColor, Pixel(pixels, width, 8, 9));
        Assert.Equal(SnapshotRenderer.PersonColor, Pixel(pixels, width, 9, 10));
        Assert.Equal(SnapshotRenderer.PersonColor, Pixel(pixels, width, 23, 17));
        Assert.Equal(((byte)114, (byte)114, (byte)114), Pixel(pixels, width, 12, 13));   // kutunun içi
        Assert.Equal(((byte)114, (byte)114, (byte)114), Pixel(pixels, width, 7, 9));     // kutunun dışı

        var bmp = SnapshotRenderer.RenderBmp(frame, geometry, detections);
        Assert.Equal(32, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(18)));
        Assert.Equal(18, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(22)));
    }

    private static (byte, byte, byte) Pixel(byte[] pixels, int width, int x, int y)
    {
        var offset = ((y * width) + x) * 3;
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }
}
