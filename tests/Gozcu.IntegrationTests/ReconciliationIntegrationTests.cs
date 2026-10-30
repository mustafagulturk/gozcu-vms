using Gozcu.Contracts.Events;
using Gozcu.Infrastructure.Messaging;
using Gozcu.Infrastructure.Persistence;
using Gozcu.IntegrationTests.Infrastructure;
using Gozcu.Media.Ffmpeg;
using Gozcu.Media.Probing;
using Gozcu.Media.Processes;
using Gozcu.RecordingService;
using Gozcu.RecordingService.Reconciliation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Gozcu.IntegrationTests;

/// <summary>Gerçek ffprobe'un geçerli ve çökmüş (moov atomu olmayan) MP4'leri ayırt ettiğini doğrular.</summary>
public sealed class ReconciliationProbeIntegrationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("gozcu-probe-").FullName;
    private readonly FfprobeMediaProbe _probe = new(new ProcessRunner());

    [Fact]
    public async Task Valid_mp4_is_playable_with_its_duration_and_codec()
    {
        var path = Path.Combine(_directory, "valid.mp4");
        await TestMedia.WriteValidAsync(path, seconds: 3);

        var result = await _probe.ProbeAsync(path, CancellationToken.None);

        Assert.True(result.IsPlayable, result.Error);
        Assert.Equal("h264", result.VideoCodec);
        Assert.InRange(result.Duration.TotalSeconds, 2.9, 3.1);
    }

    [Fact]
    public async Task Mp4_of_killed_ffmpeg_is_not_playable()
    {
        var path = Path.Combine(_directory, "killed.mp4");
        await TestMedia.WriteKilledAsync(path);

        var result = await _probe.ProbeAsync(path, CancellationToken.None);

        Assert.True(new FileInfo(path).Length >= 8 * 1024);
        Assert.False(result.IsPlayable);
        Assert.False(result.Readable);
        Assert.Contains("moov", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mp4_with_truncated_moov_is_not_playable()
    {
        var path = Path.Combine(_directory, "truncated.mp4");
        await TestMedia.WriteTruncatedAsync(path);

        var result = await _probe.ProbeAsync(path, CancellationToken.None);

        Assert.False(result.IsPlayable);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

/// <summary>Açılış uzlaştırmasının PostgreSQL ve gerçek ffprobe ile uçtan uca davranışı.</summary>
public sealed class ReconciliationIntegrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly string _root = Directory.CreateTempSubdirectory("gozcu-reconcile-it-").FullName;
    private NpgsqlDataSource _dataSource = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _dataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        await new DatabaseMigrator(_dataSource, NullLogger<DatabaseMigrator>.Instance).MigrateAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _postgres.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Orphan_is_indexed_crashed_file_quarantined_and_missing_row_marked_deleted()
    {
        var repository = new SegmentRepository(_dataSource);

        // Önceki çalışma: 10:00 parçası indekslenmiş; 10:01 bitmiş ama kuyrukta kalmış (yetim);
        // 10:02 yazılırken servis SIGKILL ile öldürülmüş; 10:03 bağlantı açılır açılmaz kopmuş (boş).
        var indexed = SegmentPath("cam1", 0);
        var orphan = SegmentPath("cam1", 1);
        var crashed = SegmentPath("cam1", 2);
        var tiny = SegmentPath("cam1", 3);
        await TestMedia.WriteValidAsync(indexed, seconds: 2);
        await TestMedia.WriteValidAsync(orphan, seconds: 3);
        await TestMedia.WriteKilledAsync(crashed);
        await File.WriteAllBytesAsync(tiny, new byte[48]);

        await repository.UpsertAsync("cam1", indexed, T0, T0.AddSeconds(2), new FileInfo(indexed).Length, CancellationToken.None);
        // Dosyası saklama dışında (elle) silinmiş bir parça.
        var removed = SegmentPath("cam2", 0);
        await repository.UpsertAsync("cam2", removed, T0, T0.AddMinutes(1), 123_456, CancellationToken.None);

        var readiness = new DatabaseReadiness();
        var publisher = new CollectingPublisher();
        var report = new ReconciliationReport();
        using var service = new StartupReconciliationService(
            Options.Create(new RecordingOptions { RootPath = _root }),
            new FfprobeMediaProbe(new ProcessRunner()),
            repository,
            readiness,
            publisher,
            report,
            TimeProvider.System,
            NullLogger<StartupReconciliationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        Assert.Equal(ReconciliationState.WaitingForDatabase, report.Snapshot().State);

        // Disk listesi alındıktan sonra bu çalışmanın kayıt süreci yeni bir parçaya başlıyor (henüz moov yok);
        // listede olmadığı için dokunulmamalı.
        var current = SegmentPath("cam1", 90);
        await File.WriteAllBytesAsync(current, new byte[64 * 1024]);

        readiness.MarkReady();
        await Eventually.TrueAsync(
            () => report.Snapshot().State is ReconciliationState.Completed or ReconciliationState.Failed,
            TimeSpan.FromSeconds(30), "uzlaştırma bitmeli");
        await service.StopAsync(CancellationToken.None);

        var status = report.Snapshot();
        Assert.Equal(ReconciliationState.Completed, status.State);
        Assert.Equal(
            (4, 1, 2, 2, 1, 1, 1, 1, 0),
            (status.Scanned, status.AlreadyIndexed, status.Probed, status.Valid, status.Indexed, status.Quarantined, status.RemovedTiny, status.MarkedDeleted, status.Skipped));
        Assert.NotNull(status.CompletedAt);

        var active = await repository.ListActiveAsync(CancellationToken.None);
        Assert.Equal([indexed, orphan], active.Select(s => s.FilePath));
        var orphanRow = active[1];
        Assert.Equal(T0.AddMinutes(1), orphanRow.StartedAt);
        Assert.InRange((orphanRow.EndedAt - orphanRow.StartedAt).TotalSeconds, 2.9, 3.1);
        Assert.Equal(new FileInfo(orphan).Length, orphanRow.SizeBytes);

        var quarantined = Path.Combine(_root, StartupReconciliationService.QuarantineDirectoryName, "cam1", Path.GetFileName(crashed));
        Assert.False(File.Exists(crashed));
        Assert.True(File.Exists(quarantined));
        Assert.False(File.Exists(tiny));
        Assert.True(File.Exists(current));

        var completed = Assert.Single(publisher.Events.OfType<RecordingSegmentCompleted>());
        Assert.Equal(orphanRow.Id, completed.SegmentId);
        Assert.Equal(orphan, completed.FilePath);

        // İkinci açılış hiçbir şey değiştirmez (idempotent).
        var secondReport = new ReconciliationReport();
        var secondReadiness = new DatabaseReadiness();
        secondReadiness.MarkReady();
        File.Delete(current);
        using var second = new StartupReconciliationService(
            Options.Create(new RecordingOptions { RootPath = _root }),
            new FfprobeMediaProbe(new ProcessRunner()),
            repository, secondReadiness, publisher, secondReport, TimeProvider.System,
            NullLogger<StartupReconciliationService>.Instance);
        await second.StartAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => secondReport.Snapshot().State == ReconciliationState.Completed, TimeSpan.FromSeconds(30), "ikinci uzlaştırma bitmeli");
        await second.StopAsync(CancellationToken.None);

        var again = secondReport.Snapshot();
        Assert.Equal((2, 2, 0, 0, 0, 0), (again.Scanned, again.AlreadyIndexed, again.Probed, again.Indexed, again.Quarantined, again.MarkedDeleted));
        Assert.Single(publisher.Events);
    }

    private string SegmentPath(string camera, int minute) =>
        Path.Combine(_root, camera, SegmentFileName.Format(camera, T0.AddMinutes(minute)));

    private sealed class CollectingPublisher : IEventPublisher
    {
        private readonly List<IntegrationEvent> _events = [];

        public IReadOnlyList<IntegrationEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public Task PublishAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
        {
            lock (_events)
            {
                _events.Add(integrationEvent);
            }

            return Task.CompletedTask;
        }
    }
}
