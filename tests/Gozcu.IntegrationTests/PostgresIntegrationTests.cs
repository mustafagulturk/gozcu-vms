using Gozcu.Contracts.Cameras;
using Gozcu.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Gozcu.IntegrationTests;

public sealed class PostgresIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
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
    }

    [Fact]
    public async Task Migrations_are_idempotent_and_safe_to_run_concurrently()
    {
        var migrator = new DatabaseMigrator(_dataSource, NullLogger<DatabaseMigrator>.Instance);
        await Task.WhenAll(migrator.MigrateAsync(CancellationToken.None), migrator.MigrateAsync(CancellationToken.None));

        await using var command = _dataSource.CreateCommand("SELECT count(*) FROM schema_migrations");
        Assert.Equal(DatabaseMigrator.LoadScripts().Count(), Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Segments_can_be_searched_by_camera_and_time_range()
    {
        var repository = new SegmentRepository(_dataSource);
        var t0 = new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 5; i++)
        {
            var start = t0.AddMinutes(i);
            await repository.UpsertAsync("cam1", $"/r/cam1/{i}.mp4", start, start.AddMinutes(1), 1000 + i, CancellationToken.None);
        }

        await repository.UpsertAsync("cam2", "/r/cam2/0.mp4", t0, t0.AddMinutes(1), 1, CancellationToken.None);

        // 10:01:30 – 10:03:00 (UTC+3 olarak verilir) aralığıyla kesişen parçalar: 10:01 ve 10:02.
        var turkey = TimeSpan.FromHours(3);
        var found = await repository.FindAsync(
            "cam1", t0.AddSeconds(90).ToOffset(turkey), t0.AddMinutes(3).ToOffset(turkey), 100, CancellationToken.None);

        Assert.Equal(["/r/cam1/1.mp4", "/r/cam1/2.mp4"], found.Select(s => s.FilePath));
        Assert.Equal(t0.AddMinutes(1), found[0].StartedAt);
        Assert.Equal(1001, found[0].SizeBytes);
    }

    [Fact]
    public async Task Upsert_is_idempotent_and_deleted_segments_are_hidden()
    {
        var repository = new SegmentRepository(_dataSource);
        var t0 = new DateTimeOffset(2026, 10, 28, 11, 0, 0, TimeSpan.Zero);

        var id1 = await repository.UpsertAsync("cam3", "/r/cam3/a.mp4", t0, t0.AddMinutes(1), 10, CancellationToken.None);
        var id2 = await repository.UpsertAsync("cam3", "/r/cam3/a.mp4", t0, t0.AddMinutes(1), 20, CancellationToken.None);
        Assert.Equal(id1, id2);

        await repository.MarkDeletedAsync(["/r/cam3/a.mp4"], DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Empty(await repository.FindAsync("cam3", null, null, 10, CancellationToken.None));
    }

    [Fact]
    public async Task Cameras_are_upserted()
    {
        var repository = new CameraRepository(_dataSource);
        var camera = new CameraDefinition { Id = "cam1", Name = "Eski", RtspUrl = new Uri("rtsp://a/cam1") };
        await repository.UpsertAsync(camera, CancellationToken.None);
        camera.Name = "Yeni";
        await repository.UpsertAsync(camera, CancellationToken.None);

        await using var command = _dataSource.CreateCommand("SELECT name FROM cameras WHERE id = 'cam1'");
        Assert.Equal("Yeni", await command.ExecuteScalarAsync());
    }
}
