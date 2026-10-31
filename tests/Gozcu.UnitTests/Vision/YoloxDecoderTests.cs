using Gozcu.Contracts.Analysis;
using Gozcu.Vision;

namespace Gozcu.UnitTests.Vision;

public class YoloxDecoderTests
{
    private const int Size = 416;
    private const int Person = 0;
    private const int Car = 2;
    private const int Truck = 7;
    private const int Dog = 16;

    private static readonly LetterboxGeometry Square = new(Size, Size, Size, Size);

    [Theory]
    [InlineData(416, 416, 3549)]
    [InlineData(640, 640, 8400)]
    [InlineData(640, 416, 5460)]
    public void Anchor_count_matches_model_output_rows(int width, int height, int expected)
    {
        Assert.Equal(expected, YoloxDecoder.AnchorCount(width, height));
    }

    [Fact]
    public void Single_strong_anchor_is_decoded_with_grid_offset_and_stride()
    {
        var output = new SyntheticOutput(Size, Size);
        // Adım 16 ızgarasında (x=5, y=10) hücresi: merkez ((0.5+5)*16, (0.5+10)*16) = (88, 168), boyut 32x64.
        output.Set(stride: 16, gridX: 5, gridY: 10, 0.5f, 0.5f, MathF.Log(2), MathF.Log(4), objectness: 0.9f, classIndex: Car, classScore: 0.8f);

        var detection = Assert.Single(YoloxDecoder.Decode(output.Values, Size, Size, 0.35f, Square));

        Assert.Equal("car", detection.Label);
        Assert.Equal(ObjectCategory.Vehicle, detection.Category);
        Assert.Equal(0.72f, detection.Confidence, 4);
        AssertBox(new BoundingBox(72f / Size, 136f / Size, 32f / Size, 64f / Size), detection.Box);
    }

    [Theory]
    [InlineData(8, 0, 0)]
    [InlineData(8, 51, 51)]
    [InlineData(32, 0, 0)]
    [InlineData(32, 12, 12)]
    public void Rows_are_ordered_by_stride_then_row_major(int stride, int gridX, int gridY)
    {
        var output = new SyntheticOutput(Size, Size);
        output.Set(stride, gridX, gridY, 0.5f, 0.5f, 0, 0, objectness: 1f, classIndex: Person, classScore: 1f);

        var detection = Assert.Single(YoloxDecoder.Decode(output.Values, Size, Size, 0.35f, Square));

        var centerX = (gridX + 0.5f) * stride / Size;
        var centerY = (gridY + 0.5f) * stride / Size;
        Assert.Equal(ObjectCategory.Person, detection.Category);
        Assert.Equal(centerX, detection.Box.X + (detection.Box.Width / 2), 4);
        Assert.Equal(centerY, detection.Box.Y + (detection.Box.Height / 2), 4);
    }

    [Fact]
    public void Box_is_mapped_through_letterbox_geometry()
    {
        var geometry = new LetterboxGeometry(Size, Size, 1280, 720);
        var output = new SyntheticOutput(Size, Size);
        // Merkez (208, 117) = içerik alanının (416x234) tam ortası; boyut 128x64. Dikeyde dolgu değil içerik yüksekliğine bölünür.
        output.Set(stride: 32, gridX: 6, gridY: 3, 0.5f, 0.65625f, MathF.Log(4), MathF.Log(2), objectness: 1f, classIndex: Person, classScore: 0.9f);

        var detection = Assert.Single(YoloxDecoder.Decode(output.Values, Size, Size, 0.35f, geometry));

        AssertBox(new BoundingBox(144f / 416, 85f / 234, 128f / 416, 64f / 234), detection.Box);
    }

    [Fact]
    public void Best_class_wins_and_non_target_classes_are_ignored()
    {
        var output = new SyntheticOutput(Size, Size);
        // Köpek en yüksek skorlu sınıf: insan skoru da yüksek olsa bile tespit "dog" sayılır ve atılır.
        output.Set(stride: 8, gridX: 3, gridY: 3, 0.5f, 0.5f, 1, 1, objectness: 0.95f, classIndex: Dog, classScore: 0.9f);
        output.SetClass(stride: 8, gridX: 3, gridY: 3, Person, 0.6f);
        // Kamyon araç kategorisine eşlenir.
        output.Set(stride: 8, gridX: 30, gridY: 30, 0.5f, 0.5f, 1, 1, objectness: 0.9f, classIndex: Truck, classScore: 0.9f);

        var detection = Assert.Single(YoloxDecoder.Decode(output.Values, Size, Size, 0.35f, Square));

        Assert.Equal("truck", detection.Label);
        Assert.Equal(ObjectCategory.Vehicle, detection.Category);
    }

    [Fact]
    public void Score_is_objectness_times_class_score_and_must_reach_threshold()
    {
        var output = new SyntheticOutput(Size, Size);
        output.Set(stride: 16, gridX: 1, gridY: 1, 0.5f, 0.5f, 1, 1, objectness: 0.5f, classIndex: Person, classScore: 0.6f);   // 0.30
        output.Set(stride: 16, gridX: 10, gridY: 10, 0.5f, 0.5f, 1, 1, objectness: 0.3f, classIndex: Person, classScore: 1f);   // nesnellik düşük
        output.Set(stride: 16, gridX: 20, gridY: 20, 0.5f, 0.5f, 1, 1, objectness: 0.7f, classIndex: Person, classScore: 0.6f); // 0.42

        var detection = Assert.Single(YoloxDecoder.Decode(output.Values, Size, Size, 0.35f, Square));

        Assert.Equal(0.42f, detection.Confidence, 4);
    }

    [Fact]
    public void Box_entirely_in_padding_is_dropped()
    {
        var geometry = new LetterboxGeometry(Size, Size, 1280, 720);
        var output = new SyntheticOutput(Size, Size);
        output.Set(stride: 32, gridX: 6, gridY: 11, 0.5f, 0.5f, 0, 0, objectness: 1f, classIndex: Car, classScore: 1f);

        Assert.Empty(YoloxDecoder.Decode(output.Values, Size, Size, 0.35f, geometry));
    }

    [Fact]
    public void Wrong_output_length_is_rejected()
    {
        var output = new float[100 * YoloxDecoder.ValuesPerAnchor];

        var error = Assert.Throws<ArgumentException>(() => YoloxDecoder.Decode(output, Size, Size, 0.35f, Square));
        Assert.Contains("3549", error.Message, StringComparison.Ordinal);
    }

    private static void AssertBox(BoundingBox expected, BoundingBox actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Width, actual.Width, 4);
        Assert.Equal(expected.Height, actual.Height, 4);
    }

    /// <summary>Sıfırlarla dolu bir YOLOX çıktısı; belirli ızgara hücrelerine elle değer yazılır.</summary>
    private sealed class SyntheticOutput(int width, int height)
    {
        public float[] Values { get; } = new float[YoloxDecoder.AnchorCount(width, height) * YoloxDecoder.ValuesPerAnchor];

        public void Set(int stride, int gridX, int gridY, float dx, float dy, float logW, float logH, float objectness, int classIndex, float classScore)
        {
            var row = Row(stride, gridX, gridY);
            float[] box = [dx, dy, logW, logH, objectness];
            box.CopyTo(Values, row);
            Values[row + 5 + classIndex] = classScore;
        }

        public void SetClass(int stride, int gridX, int gridY, int classIndex, float classScore) =>
            Values[Row(stride, gridX, gridY) + 5 + classIndex] = classScore;

        private int Row(int stride, int gridX, int gridY)
        {
            var offset = 0;
            foreach (var s in new[] { 8, 16, 32 })
            {
                if (s == stride)
                {
                    return (offset + (gridY * (width / s)) + gridX) * YoloxDecoder.ValuesPerAnchor;
                }

                offset += width / s * (height / s);
            }

            throw new ArgumentOutOfRangeException(nameof(stride));
        }
    }
}
