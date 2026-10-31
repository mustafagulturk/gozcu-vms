using Gozcu.Contracts.Analysis;
using Gozcu.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Gozcu.IntegrationTests.Analysis;

public sealed class DetectionRepositoryIntegrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 10, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private NpgsqlDataSource _dataSource = null!;
    private DetectionRepository _repository = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _dataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        await new DatabaseMigrator(_dataSource, NullLogger<DatabaseMigrator>.Instance).MigrateAsync(CancellationToken.None);
        _repository = new DetectionRepository(_dataSource);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Frames_are_batch_inserted_and_queried_newest_first_with_jsonb_round_trip()
    {
        var frames = Enumerable.Range(0, 250)
            .Select(i => new DetectionFrameRecord(
                "cam1",
                T0.AddMilliseconds(500 * i),
                12.5f + i,
                [new StoredDetection("car", ObjectCategory.Vehicle, 0.91f, 0.1f, 0.2f, 0.3f, 0.4f), Person(0.7f)]))
            .ToList();
        await _repository.InsertFramesAsync(frames, CancellationToken.None);
        await _repository.InsertFramesAsync([new DetectionFrameRecord("cam2", T0, 1, [Person(0.5f)])], CancellationToken.None);

        // UTC+3 olarak verilen aralık: 10:00:10 – 10:00:20 → 20 kare, en yenisi önce.
        var turkey = TimeSpan.FromHours(3);
        var found = await _repository.FindFramesAsync("cam1", T0.AddSeconds(10).ToOffset(turkey), T0.AddSeconds(20).ToOffset(turkey), 100, CancellationToken.None);

        Assert.Equal(20, found.Count);
        Assert.Equal(T0.AddSeconds(19.5), found[0].CapturedAt);
        Assert.Equal(T0.AddSeconds(10), found[^1].CapturedAt);
        Assert.Equal(12.5f + 39, found[0].InferenceMs);
        Assert.Equal(frames[39].Detections, found[0].Detections);

        var limited = await _repository.FindFramesAsync("cam1", null, null, 5, CancellationToken.None);
        Assert.Equal(T0.AddMilliseconds(500 * 249), limited[0].CapturedAt);
        Assert.Equal(5, limited.Count);

        // jsonb biçimi belgelenen alan adlarını kullanır ve sorgulanabilir.
        await using var command = _dataSource.CreateCommand(
            "SELECT count(*) FROM detection_frames WHERE detections @> '[{\"category\":\"Vehicle\",\"label\":\"car\"}]' AND (detections->0->>'w')::real = 0.3::real");
        Assert.Equal(250L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Event_is_opened_updated_and_closed_by_upsert()
    {
        var id = Guid.NewGuid();
        var opened = new DetectionEventRecord(id, "cam3", ObjectCategory.Person, T0, null, 1, 0.6f, 2);
        await _repository.UpsertEventsAsync([opened], CancellationToken.None);

        var open = Assert.Single(await _repository.FindEventsAsync("cam3", null, null, null, 10, CancellationToken.None));
        Assert.Equal(opened, open);

        await _repository.UpsertEventsAsync([opened with { MaxCount = 3, PeakConfidence = 0.9f, FrameCount = 10 }], CancellationToken.None);
        await _repository.UpsertEventsAsync(
            [opened with { EndedAt = T0.AddSeconds(30), MaxCount = 3, PeakConfidence = 0.9f, FrameCount = 12 }], CancellationToken.None);

        // Kapanmış olayın bitişi gecikmiş bir ara güncellemeyle silinmez, sayılar geri gitmez.
        await _repository.UpsertEventsAsync([opened with { FrameCount = 11 }], CancellationToken.None);

        var closed = Assert.Single(await _repository.FindEventsAsync("cam3", ObjectCategory.Person, null, null, 10, CancellationToken.None));
        Assert.Equal(T0.AddSeconds(30), closed.EndedAt);
        Assert.Equal(3, closed.MaxCount);
        Assert.Equal(0.9f, closed.PeakConfidence);
        Assert.Equal(12, closed.FrameCount);
        Assert.Empty(await _repository.FindEventsAsync("cam3", ObjectCategory.Vehicle, null, null, 10, CancellationToken.None));
    }

    [Fact]
    public async Task Events_are_filtered_by_time_overlap_and_returned_newest_first()
    {
        await _repository.UpsertEventsAsync(
        [
            new(Guid.NewGuid(), "cam4", ObjectCategory.Vehicle, T0, T0.AddMinutes(1), 1, 0.8f, 3),
            new(Guid.NewGuid(), "cam4", ObjectCategory.Vehicle, T0.AddMinutes(5), T0.AddMinutes(6), 2, 0.8f, 3),
            new(Guid.NewGuid(), "cam4", ObjectCategory.Person, T0.AddMinutes(10), null, 1, 0.8f, 3),
        ], CancellationToken.None);

        var all = await _repository.FindEventsAsync("cam4", null, null, null, 10, CancellationToken.None);
        Assert.Equal([T0.AddMinutes(10), T0.AddMinutes(5), T0], all.Select(e => e.StartedAt));

        // 10:00:30 – 10:05:30 ile kesişenler: ilk ikisi. Süren olay "from"dan sonra da kesişir.
        var range = await _repository.FindEventsAsync("cam4", null, T0.AddSeconds(30), T0.AddMinutes(5.5), 10, CancellationToken.None);
        Assert.Equal([T0.AddMinutes(5), T0], range.Select(e => e.StartedAt));
        var ongoing = await _repository.FindEventsAsync("cam4", null, T0.AddHours(1), null, 10, CancellationToken.None);
        Assert.Null(Assert.Single(ongoing).EndedAt);
    }

    [Fact]
    public async Task Dangling_open_events_are_closed_at_last_matching_frame_on_startup()
    {
        var withFrames = Guid.NewGuid();
        var withoutFrames = Guid.NewGuid();
        var alreadyClosed = Guid.NewGuid();
        await _repository.UpsertEventsAsync(
        [
            new(withFrames, "crash", ObjectCategory.Person, T0, null, 1, 0.8f, 2),
            new(withoutFrames, "crash", ObjectCategory.Vehicle, T0.AddSeconds(2), null, 1, 0.8f, 2),
            new(alreadyClosed, "crash", ObjectCategory.Person, T0.AddHours(-1), T0.AddHours(-1).AddSeconds(5), 1, 0.8f, 2),
        ], CancellationToken.None);
        await _repository.InsertFramesAsync(
        [
            new("crash", T0.AddSeconds(-5), 1, [Person(0.9f)]),                 // olaydan önce
            new("crash", T0.AddSeconds(1), 1, [Person(0.9f)]),
            new("crash", T0.AddSeconds(3), 1, [Person(0.9f)]),
            new("crash", T0.AddSeconds(4), 1, [Person(0.2f)]),                 // eşik altı da olsa insan görülmüş
            new("other", T0.AddSeconds(9), 1, [Person(0.9f)]),                 // başka kamera
        ], CancellationToken.None);

        var closed = await _repository.CloseDanglingEventsAsync(CancellationToken.None);

        Assert.Equal(2, closed);
        var events = (await _repository.FindEventsAsync("crash", null, null, null, 10, CancellationToken.None)).ToDictionary(e => e.Id);
        Assert.Equal(T0.AddSeconds(4), events[withFrames].EndedAt);
        Assert.Equal(T0.AddSeconds(2), events[withoutFrames].EndedAt);
        Assert.Equal(T0.AddHours(-1).AddSeconds(5), events[alreadyClosed].EndedAt);
        Assert.Equal(0, await _repository.CloseDanglingEventsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Retention_deletes_only_frames_older_than_cutoff()
    {
        await _repository.InsertFramesAsync(
        [
            new("retain", T0.AddDays(-8), 1, [Person(0.9f)]),
            new("retain", T0.AddDays(-7).AddSeconds(-1), 1, [Person(0.9f)]),
            new("retain", T0.AddDays(-6), 1, [Person(0.9f)]),
            new("retain", T0, 1, [Person(0.9f)]),
        ], CancellationToken.None);

        var deleted = await _repository.DeleteFramesOlderThanAsync(T0.AddDays(-7), CancellationToken.None);

        Assert.Equal(2, deleted);
        var remaining = await _repository.FindFramesAsync("retain", null, null, 10, CancellationToken.None);
        Assert.Equal([T0, T0.AddDays(-6)], remaining.Select(f => f.CapturedAt));
    }

    private static StoredDetection Person(float confidence) => new("person", ObjectCategory.Person, confidence, 0.5f, 0.5f, 0.1f, 0.3f);
}
