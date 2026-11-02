using Gozcu.Vision;

namespace Gozcu.UnitTests.Vision;

public class LetterboxGeometryTests
{
    [Fact]
    public void Wide_source_is_scaled_to_model_width_and_padded_at_bottom()
    {
        var geometry = new LetterboxGeometry(416, 416, 1280, 720);

        Assert.Equal(0.325, geometry.Scale, 6);
        Assert.Equal(416, geometry.ContentWidth);
        Assert.Equal(234, geometry.ContentHeight);
    }

    [Fact]
    public void Tall_source_is_scaled_to_model_height_and_padded_at_right()
    {
        var geometry = new LetterboxGeometry(416, 416, 480, 640);

        Assert.Equal(0.65, geometry.Scale, 6);
        Assert.Equal(312, geometry.ContentWidth);
        Assert.Equal(416, geometry.ContentHeight);
    }

    [Fact]
    public void Model_pixels_are_normalized_by_content_area_not_model_size()
    {
        var geometry = new LetterboxGeometry(416, 416, 1280, 720);

        var box = geometry.ToNormalized(104, 58.5f, 208, 117);

        Assert.Equal(0.25f, box.X, 4);
        Assert.Equal(0.25f, box.Y, 4);
        Assert.Equal(0.5f, box.Width, 4);
        Assert.Equal(0.5f, box.Height, 4);
    }

    [Fact]
    public void Boxes_leaving_the_frame_or_entering_padding_are_clamped()
    {
        var geometry = new LetterboxGeometry(416, 416, 1280, 720);

        // Sol üstten taşan ve alttaki dolgu alanına (y > 234) uzanan kutu.
        var box = geometry.ToNormalized(-20, 200, 120, 100);

        Assert.Equal(0f, box.X);
        Assert.Equal(200f / 234, box.Y, 4);
        Assert.Equal(100f / 416, box.Width, 4);
        Assert.Equal(1f, box.Y + box.Height, 4);
    }

    [Fact]
    public void Box_entirely_in_padding_has_zero_area()
    {
        var geometry = new LetterboxGeometry(416, 416, 1280, 720);

        var box = geometry.ToNormalized(10, 300, 50, 50);

        Assert.Equal(0f, box.Area);
    }
}
