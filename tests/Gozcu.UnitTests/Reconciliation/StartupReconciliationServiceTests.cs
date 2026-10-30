using Gozcu.Contracts.Events;
using Gozcu.Infrastructure.Messaging;
using Gozcu.Infrastructure.Persistence;
using Gozcu.Media.Ffmpeg;
using Gozcu.Media.Probing;
using Gozcu.RecordingService;
using Gozcu.RecordingService.Reconciliation;
using Gozcu.RecordingService.Retention;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Gozcu.UnitTests.Reconciliation;

public sealed class StartupReconciliationServiceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);

    private readonly string _root = Directory.CreateTempSubdirectory("gozcu-reconcile-").FullName;
    private readonly FakeProbe _probe = new();
    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1");
    private readonly ReconciliationReport _report = new();
    private readonly StartupReconciliationService _service;

    public StartupReconciliationServiceTests()
    {
        _service = new StartupReconciliationService(
            Options.Create(new RecordingOptions { RootPath = _root }),
            _probe,
            new SegmentRepository(_dataSource),
            new DatabaseReadiness(),
            new NullPublisher(),
            _report,
            TimeProvider.System,
            NullLogger<StartupReconciliationService>.Instance);
    }

    [Fact]
    public void Scan_lists_camera_segments_but_not_quarantine_or_foreign_files()
    {
        var segment = Write("cam1", 0, 4096);
        Write("cam2", 0, 10);
        WriteRaw(Path.Combine("cam1", "notlar.mp4"), 4096);
        WriteRaw(Path.Combine(StartupReconciliationService.QuarantineDirectoryName, "cam1", SegmentFileName.Format("cam1", T0.AddMinutes(5))), 4096);

        var files = StartupReconciliationService.ScanRecordings(_root);

        Assert.Equal(2, files.Count);
        var cam1 = Assert.Single(files, f => f.CameraId == "cam1");
        Assert.Equal(segment, cam1.FilePath);
        Assert.Equal(T0, cam1.StartedAt);
        Assert.Equal(4096, cam1.SizeBytes);
    }

    [Fact]
    public async Task Corrupt_file_is_moved_to_quarantine_keeping_its_name_and_tiny_file_is_deleted()
    {
        var valid = Write("cam1", 0, 4096);
        var corrupt = Write("cam1", 1, 50_000);
        var tiny = Write("cam1", 2, 48);
        _probe.Results[valid] = MediaProbeResult.Playable(TimeSpan.FromSeconds(60), "h264");
        _probe.Results[corrupt] = MediaProbeResult.Unreadable("moov atom not found");

        var outcome = await ReconcileAsync([]);

        var quarantined = Path.Combine(_root, StartupReconciliationService.QuarantineDirectoryName, "cam1", Path.GetFileName(corrupt));
        Assert.False(File.Exists(corrupt));
        Assert.True(File.Exists(quarantined));
        Assert.Equal(50_000, new FileInfo(quarantined).Length);
        Assert.False(File.Exists(tiny));
        Assert.True(File.Exists(valid));
        Assert.Equal(valid, Assert.Single(outcome.ToIndex).File.FilePath);
        Assert.Equal((2, 1, 1, 0), (outcome.Probed, outcome.Quarantined, outcome.RemovedTiny, outcome.Skipped));

        // Karantinadaki dosya saklama servisinin taramasında hâlâ görünür (kotaya sayılır, sırası gelince silinir).
        Assert.Contains(RetentionService.ScanSegments(_root), s => s.FilePath == quarantined && s.CameraId == "cam1");
        // Bir sonraki açılışta karantina yeniden taranmaz.
        Assert.DoesNotContain(StartupReconciliationService.ScanRecordings(_root), f => f.FilePath == quarantined);
    }

    [Fact]
    public async Task Files_already_indexed_are_not_probed()
    {
        var indexed = Write("cam1", 0, 4096);

        var outcome = await ReconcileAsync([new RecordingSegment(1, "cam1", indexed, T0, T0.AddMinutes(1), 4096)]);

        Assert.Empty(_probe.Probed);
        Assert.Equal(1, outcome.Plan.AlreadyIndexed);
        Assert.Empty(outcome.ToIndex);
    }

    [Fact]
    public async Task File_is_left_alone_when_probe_cannot_run()
    {
        var file = Write("cam1", 0, 50_000);
        _probe.Throw = new System.ComponentModel.Win32Exception("ffprobe bulunamadı");

        var outcome = await ReconcileAsync([]);

        Assert.True(File.Exists(file));
        Assert.Equal((0, 1), (outcome.Quarantined, outcome.Skipped));
    }

    [Fact]
    public async Task File_changed_since_snapshot_is_not_touched()
    {
        var file = Write("cam1", 0, 50_000);
        var snapshot = StartupReconciliationService.ScanRecordings(_root);
        _probe.Results[file] = MediaProbeResult.Unreadable("moov atom not found");
        // Yeni ffmpeg süreci aynı adla yazmaya başladı.
        await File.AppendAllTextAsync(file, "yeni veri");

        var outcome = await _service.ReconcileDiskAsync(snapshot, [], T0.AddHours(1), CancellationToken.None);

        Assert.True(File.Exists(file));
        Assert.Equal((0, 1), (outcome.Quarantined, outcome.Skipped));
    }

    [Fact]
    public async Task Probes_run_with_bounded_parallelism()
    {
        for (var i = 0; i < 40; i++)
        {
            Write("cam1", i, 4096);
        }

        _probe.Delay = TimeSpan.FromMilliseconds(20);

        var outcome = await ReconcileAsync([]);

        Assert.Equal(40, outcome.Probed);
        Assert.InRange(_probe.MaxConcurrency, 2, StartupReconciliationService.ProbeParallelism);
    }

    [Fact]
    public async Task Start_takes_snapshot_without_waiting_for_database()
    {
        Write("cam1", 0, 4096);
        Write("cam1", 1, 4096);

        await _service.StartAsync(CancellationToken.None);
        var status = _report.Snapshot();
        await _service.StopAsync(CancellationToken.None);

        Assert.Equal(ReconciliationState.WaitingForDatabase, status.State);
        Assert.Equal(2, status.Scanned);
        Assert.NotNull(status.StartedAt);
        Assert.Empty(_probe.Probed);
    }

    public void Dispose()
    {
        _service.Dispose();
        _dataSource.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private Task<DiskOutcome> ReconcileAsync(IReadOnlyCollection<RecordingSegment> rows) =>
        _service.ReconcileDiskAsync(StartupReconciliationService.ScanRecordings(_root), rows, T0.AddHours(1), CancellationToken.None);

    private string Write(string camera, int minute, int size) =>
        WriteRaw(Path.Combine(camera, SegmentFileName.Format(camera, T0.AddMinutes(minute))), size);

    private string WriteRaw(string relative, int size)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private sealed class FakeProbe : IMediaProbe
    {
        private int _current;

        public Dictionary<string, MediaProbeResult> Results { get; } = [];

        public List<string> Probed { get; } = [];

        public Exception? Throw { get; set; }

        public TimeSpan Delay { get; set; }

        public int MaxConcurrency { get; private set; }

        public async Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref _current);
            lock (Probed)
            {
                Probed.Add(path);
                MaxConcurrency = Math.Max(MaxConcurrency, current);
            }

            try
            {
                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, cancellationToken);
                }

                if (Throw is not null)
                {
                    throw Throw;
                }

                lock (Probed)
                {
                    return Results.TryGetValue(path, out var result) ? result : MediaProbeResult.Playable(TimeSpan.FromSeconds(60), "h264");
                }
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }
    }

    private sealed class NullPublisher : IEventPublisher
    {
        public Task PublishAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
