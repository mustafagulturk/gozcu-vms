using Gozcu.RecordingService;
using Gozcu.RecordingService.Retention;

namespace Gozcu.UnitTests.Recording;

public class RetentionPlannerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly HashSet<string> None = [];

    [Fact]
    public void Nothing_is_deleted_when_under_quota_and_disk_has_space()
    {
        var segments = Segments(5, size: 100);
        var plan = RetentionPlanner.Plan(segments, freeBytes: 10_000, Options(maxTotal: 1_000, minFree: 500), None);
        Assert.Empty(plan);
    }

    [Fact]
    public void Oldest_segments_are_deleted_until_under_quota()
    {
        var segments = Segments(10, size: 100); // toplam 1000
        var plan = RetentionPlanner.Plan(segments, freeBytes: 10_000, Options(maxTotal: 750, minFree: 0), None);

        Assert.Equal(["s0", "s1", "s2"], plan.Select(p => p.FilePath));
    }

    [Fact]
    public void Oldest_segments_are_deleted_until_enough_free_space()
    {
        var segments = Segments(10, size: 100);
        var plan = RetentionPlanner.Plan(segments, freeBytes: 150, Options(maxTotal: 0, minFree: 400), None);

        Assert.Equal(["s0", "s1", "s2"], plan.Select(p => p.FilePath));
    }

    [Fact]
    public void Ordering_is_by_time_across_cameras()
    {
        StoredSegment[] segments =
        [
            new("b-new", "camB", T0.AddMinutes(2), 100),
            new("a-old", "camA", T0, 100),
            new("b-old", "camB", T0.AddMinutes(1), 100),
        ];
        var plan = RetentionPlanner.Plan(segments, 0, Options(maxTotal: 150, minFree: 0), None);

        Assert.Equal(["a-old", "b-old"], plan.Select(p => p.FilePath));
    }

    [Fact]
    public void Protected_segments_are_never_deleted()
    {
        var segments = Segments(3, size: 100);
        var plan = RetentionPlanner.Plan(segments, 0, Options(maxTotal: 1, minFree: 0), new HashSet<string> { "s0", "s2" });

        Assert.Equal(["s1"], plan.Select(p => p.FilePath));
    }

    [Fact]
    public void Newest_segment_of_each_camera_is_protected()
    {
        StoredSegment[] segments =
        [
            new("a1", "camA", T0, 1),
            new("a2", "camA", T0.AddMinutes(1), 1),
            new("b1", "camB", T0, 1),
        ];

        Assert.Equal(new HashSet<string> { "a2", "b1" }, RetentionService.NewestPerCamera(segments));
    }

    private static StoredSegment[] Segments(int count, long size) =>
        Enumerable.Range(0, count).Select(i => new StoredSegment($"s{i}", "cam1", T0.AddMinutes(i), size)).Reverse().ToArray();

    private static RetentionOptions Options(long maxTotal, long minFree) => new() { MaxTotalBytes = maxTotal, MinFreeBytes = minFree };
}
