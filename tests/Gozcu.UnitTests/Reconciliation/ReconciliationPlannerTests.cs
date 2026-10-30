using Gozcu.Infrastructure.Persistence;
using Gozcu.Media.Probing;
using Gozcu.RecordingService.Reconciliation;

namespace Gozcu.UnitTests.Reconciliation;

public class ReconciliationPlannerTests
{
    private const long MinimumBytes = 1024;
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SnapshotAt = T0.AddHours(1);

    [Fact]
    public void Files_already_indexed_with_same_size_are_not_probed()
    {
        var indexed = File("cam1", 0, 5000);
        var resized = File("cam1", 1, 6000);
        var orphan = File("cam1", 2, 7000);
        var tiny = File("cam1", 3, 100);
        var rows = new[] { Row(indexed), Row(resized) with { SizeBytes = 1 } };

        var candidates = ReconciliationPlanner.ProbeCandidates([indexed, resized, orphan, tiny], rows, MinimumBytes);

        Assert.Equal([resized.FilePath, orphan.FilePath], candidates.Select(c => c.FilePath));
    }

    [Fact]
    public void Orphan_valid_file_is_indexed_with_end_time_from_probed_duration()
    {
        var orphan = File("cam1", 0, 5000);

        var plan = Plan([orphan], Probes((orphan, MediaProbeResult.Playable(TimeSpan.FromSeconds(59.96), "h264"))), []);

        var index = Assert.Single(plan.ToIndex);
        Assert.Equal(orphan, index.File);
        Assert.Equal(orphan.StartedAt.AddSeconds(59.96), index.EndedAt);
        Assert.Empty(plan.ToQuarantine);
        Assert.Empty(plan.ToMarkDeleted);
    }

    [Fact]
    public void File_indexed_with_different_size_is_reindexed_when_valid()
    {
        var file = File("cam1", 0, 5000);

        var plan = Plan([file], Probes((file, MediaProbeResult.Playable(TimeSpan.FromSeconds(60), "h264"))), [Row(file) with { SizeBytes = 4000 }]);

        Assert.Equal(file, Assert.Single(plan.ToIndex).File);
        Assert.Equal(0, plan.AlreadyIndexed);
    }

    [Theory]
    [InlineData(false, 0, "h264")]
    [InlineData(true, 0, "h264")]
    [InlineData(true, 60, null)]
    public void Unplayable_file_is_quarantined(bool readable, double seconds, string? codec)
    {
        var corrupt = File("cam1", 0, 50_000_000);
        var probe = new MediaProbeResult(readable, TimeSpan.FromSeconds(seconds), codec, "moov atom not found");

        var plan = Plan([corrupt], Probes((corrupt, probe)), []);

        Assert.Equal([corrupt], plan.ToQuarantine);
        Assert.Empty(plan.ToIndex);
        Assert.Empty(plan.ToMarkDeleted);
    }

    [Fact]
    public void Corrupt_file_with_stale_row_is_quarantined_and_row_marked_deleted()
    {
        var corrupt = File("cam1", 0, 5000);

        var plan = Plan([corrupt], Probes((corrupt, MediaProbeResult.Unreadable("moov atom not found"))), [Row(corrupt) with { SizeBytes = 9000 }]);

        Assert.Equal([corrupt], plan.ToQuarantine);
        Assert.Equal([corrupt.FilePath], plan.ToMarkDeleted);
    }

    [Fact]
    public void Tiny_files_are_deleted_without_probe()
    {
        var tiny = File("cam1", 0, 48);
        var tinyIndexed = File("cam1", 1, 0);

        var plan = Plan([tiny, tinyIndexed], Probes(), [Row(tinyIndexed)]);

        Assert.Equal([tiny, tinyIndexed], plan.ToDelete);
        Assert.Equal([tinyIndexed.FilePath], plan.ToMarkDeleted);
        Assert.Empty(plan.Undecided);
    }

    [Fact]
    public void Candidate_without_probe_result_is_left_untouched()
    {
        var file = File("cam1", 0, 5000);

        var plan = Plan([file], Probes(), [Row(file) with { SizeBytes = 1 }]);

        Assert.Equal([file], plan.Undecided);
        Assert.Empty(plan.ToQuarantine);
        Assert.Empty(plan.ToIndex);
        Assert.Empty(plan.ToMarkDeleted);
    }

    [Fact]
    public void Rows_whose_file_is_missing_are_marked_deleted_but_current_run_and_existing_files_are_kept()
    {
        var onDisk = File("cam1", 0, 5000);
        var missing = Row(File("cam1", 1, 5000));
        var existsElsewhere = Row(File("cam2", 0, 5000)) with { FilePath = "/eski-kok/cam2/cam2_20261028T100000Z.mp4" };
        // Disk listesinden sonra başlayan parça bu çalışmanın kaydıdır; listede olmaması normaldir.
        var currentRun = new RecordingSegment(99, "cam1", "/r/cam1/cam1_20261028T110005Z.mp4", SnapshotAt.AddSeconds(5), SnapshotAt.AddSeconds(65), 5000);

        var plan = ReconciliationPlanner.Plan(
            [onDisk], Probes(), [Row(onDisk), missing, existsElsewhere, currentRun], SnapshotAt, MinimumBytes,
            path => path == existsElsewhere.FilePath);

        Assert.Equal([missing.FilePath], plan.ToMarkDeleted);
        Assert.Equal(1, plan.AlreadyIndexed);
    }

    [Fact]
    public void Plan_handles_thousands_of_files()
    {
        var files = Enumerable.Range(0, 20_000).Select(i => File("cam1", i, 5000)).ToList();
        var rows = files.Where((_, i) => i % 2 == 0).Select(Row).ToList();
        var probes = files.Where((_, i) => i % 2 == 1)
            .ToDictionary(f => f.FilePath, _ => MediaProbeResult.Playable(TimeSpan.FromSeconds(60), "h264"));

        var plan = ReconciliationPlanner.Plan(files, probes, rows, SnapshotAt.AddDays(30), MinimumBytes, _ => false);

        Assert.Equal(10_000, plan.AlreadyIndexed);
        Assert.Equal(10_000, plan.ToIndex.Count);
    }

    private static ReconciliationPlan Plan(
        IReadOnlyCollection<DiskSegment> files, Dictionary<string, MediaProbeResult> probes, IReadOnlyCollection<RecordingSegment> rows) =>
        ReconciliationPlanner.Plan(files, probes, rows, SnapshotAt, MinimumBytes, _ => false);

    private static Dictionary<string, MediaProbeResult> Probes(params (DiskSegment File, MediaProbeResult Result)[] items) =>
        items.ToDictionary(i => i.File.FilePath, i => i.Result);

    private static DiskSegment File(string camera, int minute, long size)
    {
        var start = T0.AddMinutes(minute);
        return new DiskSegment($"/r/{camera}/{Gozcu.Media.Ffmpeg.SegmentFileName.Format(camera, start)}", camera, start, size, DateTime.UnixEpoch);
    }

    private static RecordingSegment Row(DiskSegment file) =>
        new(file.FilePath.GetHashCode(StringComparison.Ordinal), file.CameraId, file.FilePath, file.StartedAt, file.StartedAt.AddMinutes(1), file.SizeBytes);
}
