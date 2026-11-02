using Gozcu.Vision;

namespace Gozcu.UnitTests.Vision;

public class BoundingBoxTests
{
    [Fact]
    public void Identical_boxes_have_iou_of_one()
    {
        var box = new BoundingBox(0.1f, 0.2f, 0.3f, 0.4f);

        Assert.Equal(1f, box.IntersectionOverUnion(box), 5);
    }

    [Fact]
    public void Disjoint_and_touching_boxes_have_iou_of_zero()
    {
        var a = new BoundingBox(0, 0, 0.2f, 0.2f);

        Assert.Equal(0f, a.IntersectionOverUnion(new BoundingBox(0.5f, 0.5f, 0.2f, 0.2f)));
        Assert.Equal(0f, a.IntersectionOverUnion(new BoundingBox(0.2f, 0, 0.2f, 0.2f)));
    }

    [Fact]
    public void Half_overlapping_boxes_have_iou_of_one_third()
    {
        var a = new BoundingBox(0, 0, 0.4f, 0.4f);
        var b = new BoundingBox(0.2f, 0, 0.4f, 0.4f);

        // Kesişim 0.08, birleşim 0.16 + 0.16 - 0.08 = 0.24.
        Assert.Equal(1f / 3, a.IntersectionOverUnion(b), 5);
        Assert.Equal(a.IntersectionOverUnion(b), b.IntersectionOverUnion(a), 6);
    }

    [Fact]
    public void Degenerate_boxes_do_not_divide_by_zero()
    {
        var empty = new BoundingBox(0.5f, 0.5f, 0, 0);

        Assert.Equal(0f, empty.IntersectionOverUnion(empty));
    }
}
