using Gozcu.Contracts.Analysis;
using Gozcu.Vision;

namespace Gozcu.UnitTests.Vision;

public class NonMaxSuppressionTests
{
    private static readonly BoundingBox Box = new(0.1f, 0.1f, 0.4f, 0.4f);
    private static readonly BoundingBox Shifted = new(0.12f, 0.1f, 0.4f, 0.4f); // Box ile IoU ≈ 0.90
    private static readonly BoundingBox HalfOverlap = new(0.3f, 0.1f, 0.4f, 0.4f); // Box ile IoU = 1/3

    [Fact]
    public void Overlapping_boxes_of_same_class_keep_only_the_most_confident()
    {
        var result = NonMaxSuppression.Apply([Car(0.6f, Shifted), Car(0.9f, Box)], 0.45f, 100);

        var kept = Assert.Single(result);
        Assert.Equal(0.9f, kept.Confidence);
    }

    [Fact]
    public void Same_class_boxes_below_iou_threshold_are_both_kept()
    {
        var result = NonMaxSuppression.Apply([Car(0.9f, Box), Car(0.6f, HalfOverlap)], 0.45f, 100);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Different_classes_do_not_suppress_each_other()
    {
        var bicycle = new Detection("bicycle", ObjectCategory.Vehicle, 0.7f, Shifted);
        var person = new Detection("person", ObjectCategory.Person, 0.8f, Box);

        var result = NonMaxSuppression.Apply([bicycle, Car(0.9f, Box), person], 0.45f, 100);

        Assert.Equal(["car", "person", "bicycle"], result.Select(d => d.Label));
    }

    [Fact]
    public void Suppressed_box_does_not_suppress_others()
    {
        // B, A tarafından bastırılır; C yalnızca B ile örtüşür, bu yüzden kalmalı (açgözlü NMS).
        var a = Car(0.9f, new BoundingBox(0.0f, 0, 0.4f, 0.4f));
        var b = Car(0.8f, new BoundingBox(0.05f, 0, 0.4f, 0.4f));
        var c = Car(0.7f, new BoundingBox(0.25f, 0, 0.4f, 0.4f));

        var result = NonMaxSuppression.Apply([c, b, a], 0.45f, 100);

        Assert.Equal([0.9f, 0.7f], result.Select(d => d.Confidence));
    }

    [Fact]
    public void Result_is_sorted_by_confidence_and_capped()
    {
        var candidates = Enumerable.Range(0, 10)
            .Select(i => Car(0.4f + (i * 0.05f), new BoundingBox(i * 0.1f, 0, 0.05f, 0.05f)))
            .ToList();

        var result = NonMaxSuppression.Apply(candidates, 0.45f, 3);

        Assert.Equal([0.85f, 0.8f, 0.75f], result.Select(d => d.Confidence).Select(c => MathF.Round(c, 2)));
    }

    [Fact]
    public void Empty_input_gives_empty_result()
    {
        Assert.Empty(NonMaxSuppression.Apply([], 0.45f, 100));
    }

    private static Detection Car(float confidence, BoundingBox box) => new("car", ObjectCategory.Vehicle, confidence, box);
}
