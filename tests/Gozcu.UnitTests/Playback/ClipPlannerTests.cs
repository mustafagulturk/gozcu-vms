using Gozcu.Media.Ffmpeg;
using Gozcu.RecordingService.Playback;
using static Gozcu.UnitTests.Playback.PlaybackPlannerTests;

namespace Gozcu.UnitTests.Playback;

public class ClipPlannerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void First_segment_gets_inpoint_and_last_gets_outpoint()
    {
        var segments = new[] { Segment(1, 0, 60), Segment(2, 60, 120), Segment(3, 120, 180) };

        var entries = ClipPlanner.Plan(segments, T0.AddSeconds(15), T0.AddSeconds(150));

        Assert.Equal(
            [
                new ConcatEntry("/recordings/cam1/s1.mp4", TimeSpan.FromSeconds(15), null),
                new ConcatEntry("/recordings/cam1/s2.mp4", null, null),
                new ConcatEntry("/recordings/cam1/s3.mp4", null, TimeSpan.FromSeconds(30)),
            ],
            entries);
    }

    [Fact]
    public void Range_inside_single_segment_gets_both_points()
    {
        var entries = ClipPlanner.Plan([Segment(1, 0, 60)], T0.AddSeconds(10), T0.AddSeconds(20));

        Assert.Equal([new ConcatEntry("/recordings/cam1/s1.mp4", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20))], entries);
    }

    [Fact]
    public void Exact_segment_bounds_need_no_points_and_gaps_are_skipped()
    {
        var segments = new[] { Segment(2, 300, 360), Segment(1, 0, 60) };

        var entries = ClipPlanner.Plan(segments, T0, T0.AddSeconds(360));

        Assert.Equal(
            [new ConcatEntry("/recordings/cam1/s1.mp4"), new ConcatEntry("/recordings/cam1/s2.mp4")],
            entries);
    }

    [Fact]
    public void Overlapping_segment_starts_where_previous_ended()
    {
        var segments = new[] { Segment(1, 0, 60), Segment(2, 50, 110) };

        var entries = ClipPlanner.Plan(segments, T0, T0.AddSeconds(110));

        Assert.Equal(TimeSpan.FromSeconds(10), entries[1].InPoint);
        Assert.Null(entries[1].OutPoint);
    }

    [Fact]
    public void Segments_without_resolvable_file_are_skipped()
    {
        var segments = new[] { Segment(1, 0, 60), Segment(2, 60, 120) };

        var entries = ClipPlanner.Plan(segments, T0, T0.AddSeconds(120), s => s.Id == 1 ? null : "/full/s2.mp4");

        Assert.Equal([new ConcatEntry("/full/s2.mp4")], entries);
    }

    [Fact]
    public void Range_without_segments_yields_no_entries()
    {
        Assert.Empty(ClipPlanner.Plan([Segment(1, 0, 60)], T0.AddMinutes(5), T0.AddMinutes(6)));
    }

    [Fact]
    public void Validate_rejects_empty_inverted_and_too_long_ranges()
    {
        var max = TimeSpan.FromHours(1);

        Assert.NotNull(ClipPlanner.Validate(T0, T0, max));
        Assert.NotNull(ClipPlanner.Validate(T0, T0.AddSeconds(-1), max));
        Assert.Contains("1 saat", ClipPlanner.Validate(T0, T0.AddHours(1).AddSeconds(1), max), StringComparison.Ordinal);
        Assert.Null(ClipPlanner.Validate(T0, T0.AddHours(1), max));
        Assert.Throws<ArgumentException>(() => ClipPlanner.Plan([], T0, T0));
    }

    [Fact]
    public void File_name_contains_camera_and_utc_range()
    {
        var from = new DateTimeOffset(2026, 10, 30, 13, 15, 0, TimeSpan.FromHours(3));

        Assert.Equal("cam1_20261030T101500Z-20261030T102000Z.mp4", ClipPlanner.FileName("cam1", from, from.AddMinutes(5)));
    }
}
