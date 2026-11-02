using Gozcu.LoadTest.Engine;
using Gozcu.LoadTest.Scenarios;

namespace Gozcu.UnitTests.LoadTest;

public class ScenarioTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static TargetData Data() => new(
        ["cam1", "kapı 2"],
        [
            new SegmentInfo(41, "cam1", Now.AddMinutes(-20), Now.AddMinutes(-10), 5_000_000),
            new SegmentInfo(42, "kapı 2", Now.AddMinutes(-10), Now, null),
        ]);

    private static ScenarioInput Input(int seed = 1) => new(Data(), new Random(seed), Now);

    [Fact]
    public void Selector_maps_rolls_to_cumulative_weight_ranges()
    {
        var s = new WeightedSelector<string>([("a", 1), ("b", 0), ("c", 3)]);

        Assert.Equal(["a", "c"], s.Items);
        Assert.Equal(4, s.TotalWeight);
        Assert.Equal("a", s.Select(0));
        Assert.Equal("a", s.Select(0.2499));
        Assert.Equal("c", s.Select(0.25));
        Assert.Equal("c", s.Select(0.9999));
        Assert.Equal("c", s.Select(1));
        Assert.Equal(0.25, s.Probability(0), 9);
        Assert.Equal(0.75, s.Probability(1), 9);
    }

    [Fact]
    public void Selector_frequencies_follow_weights()
    {
        var s = new WeightedSelector<int>([(0, 70), (1, 20), (2, 10)]);
        var random = new Random(42);
        var counts = new int[3];
        for (var i = 0; i < 100_000; i++)
        {
            counts[s.Next(random)]++;
        }

        Assert.InRange(counts[0], 69_000, 71_000);
        Assert.InRange(counts[1], 19_000, 21_000);
        Assert.InRange(counts[2], 9_300, 10_700);
    }

    [Fact]
    public void Selector_rejects_invalid_weights()
    {
        Assert.Throws<ArgumentException>(() => new WeightedSelector<string>([("a", 0)]));
        Assert.Throws<ArgumentException>(() => new WeightedSelector<string>([("a", -1)]));
        Assert.Throws<ArgumentException>(() => new WeightedSelector<string>([("a", double.NaN)]));
    }

    [Fact]
    public void Mixed_weights_reference_existing_scenarios_and_sum_to_100()
    {
        Assert.Equal(100, ScenarioCatalog.MixedWeights.Sum(w => w.Weight));
        Assert.All(ScenarioCatalog.MixedWeights, w => Assert.Contains(w.Name, ScenarioCatalog.Names));
        Assert.DoesNotContain(ScenarioCatalog.MixedWeights, w => w.Name is ScenarioCatalog.ExportName or ScenarioCatalog.MixedName);
    }

    [Fact]
    public void Mixed_scenario_produces_requests_for_its_components_only()
    {
        var mixed = ScenarioCatalog.Get(ScenarioCatalog.MixedName);
        var input = Input(7);
        var labels = Enumerable.Range(0, 2_000).Select(_ => mixed.Next(input).Label).ToHashSet();

        Assert.Equal(mixed.Labels.ToHashSet(), labels);
        Assert.Equal(DataNeeds.Cameras | DataNeeds.Segments, mixed.Needs);
    }

    [Fact]
    public void Export_is_not_part_of_all()
    {
        Assert.False(ScenarioCatalog.Get(ScenarioCatalog.ExportName).IncludedInAll);
        Assert.DoesNotContain(ScenarioCatalog.ExportName, ScenarioCatalog.AllNames);
        Assert.Equal(ScenarioCatalog.MixedName, ScenarioCatalog.AllNames[^1]);
    }

    [Fact]
    public void Scenarios_report_why_they_cannot_run()
    {
        Assert.Null(ScenarioCatalog.Get(ScenarioCatalog.CamerasName).CannotRunReason(TargetData.Empty));
        Assert.NotNull(ScenarioCatalog.Get(ScenarioCatalog.Timeline1hName).CannotRunReason(TargetData.Empty));
        Assert.NotNull(ScenarioCatalog.Get(ScenarioCatalog.ExportName).CannotRunReason(new TargetData(["cam1"], [])));
        var unsized = new TargetData(["cam1"], [new SegmentInfo(1, "cam1", Now, Now.AddMinutes(1))]);
        Assert.NotNull(ScenarioCatalog.Get(ScenarioCatalog.SegmentMediaName).CannotRunReason(unsized));
        Assert.NotNull(ScenarioCatalog.Get(ScenarioCatalog.MixedName).CannotRunReason(unsized));
        Assert.Null(ScenarioCatalog.Get(ScenarioCatalog.ExportName).CannotRunReason(unsized));
        Assert.Null(ScenarioCatalog.Get(ScenarioCatalog.MixedName).CannotRunReason(Data()));
    }

    [Fact]
    public void Status_requests_use_real_routes()
    {
        Assert.Equal(new RequestSpec("x", ServiceTarget.Streaming, "/api/cameras"), RequestFactory.CameraStatus("x"));
        Assert.Equal(new RequestSpec("x", ServiceTarget.Recording, "/api/recordings/status"), RequestFactory.RecordingStatus("x"));
        Assert.Equal(new RequestSpec("x", ServiceTarget.Analysis, "/api/analysis/status"), RequestFactory.AnalysisStatus("x"));
    }

    [Fact]
    public void Timeline_24h_ends_now_and_spans_a_day()
    {
        var spec = RequestFactory.Timeline("t", Input(), TimeSpan.FromHours(24));

        Assert.Equal(ServiceTarget.Recording, spec.Target);
        Assert.Matches(@"^/api/playback/(cam1|kap%C4%B1%202)/timeline\?from=2026-10-31T12%3A00%3A00Z&to=2026-11-01T12%3A00%3A00Z$", spec.PathAndQuery);
    }

    [Fact]
    public void Timeline_1h_window_lies_within_last_day()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var spec = RequestFactory.Timeline("t", Input(seed), TimeSpan.FromHours(1));
            var (from, to) = FromTo(spec.PathAndQuery);

            Assert.Equal(TimeSpan.FromHours(1), to - from);
            Assert.True(to <= Now);
            Assert.True(from >= Now.AddHours(-24));
        }
    }

    [Fact]
    public void Locate_picks_time_inside_a_known_segment_of_the_same_camera()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var spec = RequestFactory.Locate("l", Input(seed));
            var match = System.Text.RegularExpressions.Regex.Match(spec.PathAndQuery, @"^/api/playback/([^/]+)/locate\?time=(.+)$");
            Assert.True(match.Success, spec.PathAndQuery);
            var camera = Uri.UnescapeDataString(match.Groups[1].Value);
            var time = DateTimeOffset.Parse(Uri.UnescapeDataString(match.Groups[2].Value), System.Globalization.CultureInfo.InvariantCulture);
            var segment = Data().Segments.Single(s => s.CameraId == camera);

            Assert.InRange(time, segment.Start, segment.End);
        }
    }

    [Fact]
    public void Segment_media_requests_one_mebibyte_range_of_a_sized_segment()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var spec = RequestFactory.SegmentMedia("m", Input(seed));

            Assert.Equal("/api/playback/segments/41/media", spec.PathAndQuery);
            Assert.NotNull(spec.RangeStart);
            Assert.Equal(RequestFactory.RangeLength - 1, spec.RangeEnd - spec.RangeStart);
            Assert.InRange(spec.RangeEnd!.Value, 0, 5_000_000 - 1);
        }
    }

    [Fact]
    public void Analysis_searches_cover_last_hour()
    {
        var detections = RequestFactory.Detections("d", Input());
        var events = RequestFactory.Events("e", Input());

        Assert.Equal(ServiceTarget.Analysis, detections.Target);
        Assert.Matches(@"^/api/analysis/detections\?cameraId=(cam1|kap%C4%B1%202)&from=2026-11-01T11%3A00%3A00Z&to=2026-11-01T12%3A00%3A00Z&limit=100$", detections.PathAndQuery);
        Assert.Equal("/api/analysis/events?from=2026-11-01T11%3A00%3A00Z&to=2026-11-01T12%3A00%3A00Z&limit=100", events.PathAndQuery);
    }

    [Fact]
    public void Event_search_queries_people_over_last_day()
    {
        var search = RequestFactory.EventSearch("s", Input());

        Assert.Equal(ServiceTarget.Analysis, search.Target);
        Assert.Equal(
            "/api/analysis/events/search?category=Person&from=2026-10-31T12%3A00%3A00Z&to=2026-11-01T12%3A00%3A00Z&minConfidence=0.5&limit=50",
            search.PathAndQuery);
    }

    [Fact]
    public void Export_requests_thirty_seconds_inside_a_segment()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var spec = RequestFactory.Export("x", Input(seed));
            Assert.Contains("/export?", spec.PathAndQuery, StringComparison.Ordinal);
            var (from, to) = FromTo(spec.PathAndQuery);

            Assert.Equal(TimeSpan.FromSeconds(30), to - from);
            Assert.True(from >= Now.AddMinutes(-20) && to <= Now);
        }
    }

    [Theory]
    [InlineData(10_000_000, 1_048_576, 0.0, 0, 1_048_575)]
    [InlineData(10_000_000, 1_048_576, 0.9999999, 8_951_424, 9_999_999)]
    [InlineData(1_048_576, 1_048_576, 0.5, 0, 1_048_575)]
    [InlineData(1_000, 1_048_576, 0.7, 0, 999)]
    [InlineData(2_000_000, 1_000_000, 1.0, 1_000_000, 1_999_999)]
    public void PickRange_stays_inside_file(long size, long length, double roll, long expectedStart, long expectedEnd)
    {
        Assert.Equal((expectedStart, expectedEnd), RequestFactory.PickRange(size, length, roll));
    }

    [Fact]
    public void PickRange_rejects_empty_file()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RequestFactory.PickRange(0, 10, 0.5));
    }

    [Fact]
    public void Pacer_schedules_requests_at_fixed_rate()
    {
        const long freq = 1_000_000;

        Assert.Equal(0, OpenModelPacer.ScheduledCount(-1, freq, 100));
        Assert.Equal(1, OpenModelPacer.ScheduledCount(0, freq, 100));
        Assert.Equal(1, OpenModelPacer.ScheduledCount(9_999, freq, 100));
        Assert.Equal(2, OpenModelPacer.ScheduledCount(10_000, freq, 100));
        Assert.Equal(101, OpenModelPacer.ScheduledCount(freq, freq, 100));
        Assert.Equal(1_000, OpenModelPacer.IntendedTimestamp(1_000, 0, freq, 100));
        Assert.Equal(1_000 + 250_000, OpenModelPacer.IntendedTimestamp(1_000, 1, freq, 4));
        Assert.Equal(428_571, OpenModelPacer.IntendedTimestamp(0, 1, freq, 7.0 / 3));
    }

    private static (DateTimeOffset From, DateTimeOffset To) FromTo(string pathAndQuery)
    {
        var match = System.Text.RegularExpressions.Regex.Match(pathAndQuery, @"from=([^&]+)&to=([^&]+)");
        Assert.True(match.Success, pathAndQuery);
        return (
            DateTimeOffset.Parse(Uri.UnescapeDataString(match.Groups[1].Value), System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(Uri.UnescapeDataString(match.Groups[2].Value), System.Globalization.CultureInfo.InvariantCulture));
    }
}
