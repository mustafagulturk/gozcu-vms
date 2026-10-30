using Gozcu.Infrastructure.Persistence;
using Gozcu.RecordingService.Playback;

namespace Gozcu.UnitTests.Playback;

public class PlaybackPlannerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Contiguous_segments_form_a_single_coverage_range()
    {
        // Parçalar arasında ffmpeg'in anahtar kare kaymasından kaynaklanan küçük farklar var.
        RecordingSegment[] segments =
        [
            Segment(1, 0, 60),
            Segment(2, 60.8, 120),
            Segment(3, 121.4, 180),
        ];

        var timeline = PlaybackPlanner.BuildTimeline(segments, T0, T0.AddMinutes(3));

        Assert.Equal([1L, 2L, 3L], timeline.Items.Select(i => i.SegmentId));
        Assert.Equal([new TimeRange(T0, T0.AddMinutes(3))], timeline.Coverage);
        Assert.Empty(timeline.Gaps);
        Assert.Equal(180, timeline.RecordedSeconds, precision: 3);
    }

    [Fact]
    public void Gaps_longer_than_tolerance_are_reported_including_leading_and_trailing()
    {
        RecordingSegment[] segments =
        [
            Segment(1, 60, 120),
            Segment(2, 180, 240),
        ];

        var timeline = PlaybackPlanner.BuildTimeline(segments, T0, T0.AddMinutes(5));

        Assert.Equal(
            [
                new TimeRange(T0, T0.AddMinutes(1)),
                new TimeRange(T0.AddMinutes(2), T0.AddMinutes(3)),
                new TimeRange(T0.AddMinutes(4), T0.AddMinutes(5)),
            ],
            timeline.Gaps);
        Assert.Equal(2, timeline.Coverage.Count);
        Assert.Equal(120, timeline.RecordedSeconds, precision: 3);
    }

    [Fact]
    public void Coverage_is_clipped_to_requested_range_but_items_keep_their_own_bounds()
    {
        RecordingSegment[] segments = [Segment(1, 0, 60), Segment(2, 60, 120)];

        var timeline = PlaybackPlanner.BuildTimeline(segments, T0.AddSeconds(30), T0.AddSeconds(90));

        Assert.Equal([new TimeRange(T0.AddSeconds(30), T0.AddSeconds(90))], timeline.Coverage);
        Assert.Equal(T0, timeline.Items[0].Start);
        Assert.Equal(T0.AddSeconds(120), timeline.Items[1].End);
        Assert.Equal(60, timeline.RecordedSeconds, precision: 3);
    }

    [Fact]
    public void Segments_outside_range_and_overlaps_are_handled()
    {
        RecordingSegment[] segments =
        [
            Segment(3, 100, 160),
            Segment(1, -120, -60), // aralık dışında
            Segment(2, 0, 120),    // 3 ile çakışıyor
        ];

        var timeline = PlaybackPlanner.BuildTimeline(segments, T0, T0.AddSeconds(200));

        Assert.Equal([2L, 3L], timeline.Items.Select(i => i.SegmentId));
        Assert.Equal([new TimeRange(T0, T0.AddSeconds(160))], timeline.Coverage);
        Assert.Equal([new TimeRange(T0.AddSeconds(160), T0.AddSeconds(200))], timeline.Gaps);
    }

    [Fact]
    public void Empty_range_has_single_gap()
    {
        var timeline = PlaybackPlanner.BuildTimeline([], T0, T0.AddHours(1));

        Assert.Empty(timeline.Items);
        Assert.Empty(timeline.Coverage);
        Assert.Equal([new TimeRange(T0, T0.AddHours(1))], timeline.Gaps);
        Assert.Equal(0, timeline.RecordedSeconds);
    }

    [Fact]
    public void Timeline_rejects_inverted_range()
    {
        Assert.Throws<ArgumentException>(() => PlaybackPlanner.BuildTimeline([], T0, T0));
    }

    [Fact]
    public void Locate_returns_containing_segment_and_offset()
    {
        RecordingSegment[] segments = [Segment(1, 0, 60), Segment(2, 60, 120)];

        var position = PlaybackPlanner.Locate(segments, T0.AddSeconds(75.5));

        Assert.NotNull(position);
        Assert.Equal(2, position.Segment.Id);
        Assert.Equal(15.5, position.OffsetSeconds, precision: 3);
        Assert.Equal(T0.AddSeconds(75.5), position.Time);
    }

    [Fact]
    public void Locate_at_segment_boundary_picks_next_segment()
    {
        RecordingSegment[] segments = [Segment(1, 0, 60), Segment(2, 60, 120)];

        var position = PlaybackPlanner.Locate(segments, T0.AddSeconds(60));

        Assert.Equal(2, position!.Segment.Id);
        Assert.Equal(0, position.OffsetSeconds);
    }

    [Fact]
    public void Locate_in_gap_jumps_to_next_segment_start()
    {
        RecordingSegment[] segments = [Segment(2, 300, 360), Segment(1, 0, 60)];

        var position = PlaybackPlanner.Locate(segments, T0.AddSeconds(100));

        Assert.Equal(2, position!.Segment.Id);
        Assert.Equal(0, position.OffsetSeconds);
        Assert.Equal(T0.AddSeconds(300), position.Time);
    }

    [Fact]
    public void Locate_after_last_segment_returns_null()
    {
        RecordingSegment[] segments = [Segment(1, 0, 60)];

        Assert.Null(PlaybackPlanner.Locate(segments, T0.AddSeconds(60)));
        Assert.Null(PlaybackPlanner.Locate([], T0));
    }

    internal static RecordingSegment Segment(long id, double startSeconds, double endSeconds, string? path = null) =>
        new(id, "cam1", path ?? $"/recordings/cam1/s{id}.mp4", T0.AddSeconds(startSeconds), T0.AddSeconds(endSeconds), 1000);
}
