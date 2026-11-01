using Gozcu.Contracts.Cameras;
using Gozcu.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Gozcu.IntegrationTests.Cameras;

/// <summary>Her test kendi boş veritabanıyla çalışır (tohumlama veritabanı genelinde tek seferliktir).</summary>
public sealed class CameraRepositoryIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private NpgsqlDataSource _dataSource = null!;
    private CameraRepository _repository = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _dataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        await new DatabaseMigrator(_dataSource, NullLogger<DatabaseMigrator>.Instance).MigrateAsync(CancellationToken.None);
        _repository = new CameraRepository(_dataSource);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Insert_list_and_find()
    {
        var inserted = await _repository.InsertAsync(Cam("cam2", "rtsp://admin:gizli@h/2"), CancellationToken.None);
        await _repository.InsertAsync(Cam("cam1", "rtsp://h/1", enabled: false), CancellationToken.None);

        Assert.True(inserted.Succeeded);
        Assert.Equal(1, inserted.Camera!.Version);
        Assert.Equal("rtsp://admin:gizli@h/2", inserted.Camera.RtspUrl);
        Assert.True(inserted.Camera.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-1));

        var all = await _repository.ListAsync(CancellationToken.None);
        Assert.Equal(["cam1", "cam2"], all.Select(c => c.Id));
        Assert.False(all[0].Enabled);

        var found = await _repository.FindAsync("cam2", CancellationToken.None);
        Assert.Equal(inserted.Camera, found);
        Assert.Equal(new Uri("rtsp://admin:gizli@h/2"), found!.ToDefinition().RtspUrl);
        Assert.Null(await _repository.FindAsync("yok", CancellationToken.None));
    }

    [Fact]
    public async Task Duplicate_id_conflicts_even_with_different_case()
    {
        await _repository.InsertAsync(Cam("cam1", "rtsp://h/1"), CancellationToken.None);

        Assert.Equal(CameraWriteStatus.Conflict, (await _repository.InsertAsync(Cam("cam1", "rtsp://h/x"), CancellationToken.None)).Status);
        Assert.Equal(CameraWriteStatus.Conflict, (await _repository.InsertAsync(Cam("CAM1", "rtsp://h/x"), CancellationToken.None)).Status);
        Assert.Single(await _repository.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Update_increments_version_and_enforces_expected_version()
    {
        await _repository.InsertAsync(Cam("cam1", "rtsp://admin:gizli@h/1"), CancellationToken.None);

        // Adres ve etkinlik verilmezse korunur.
        var renamed = await _repository.UpdateAsync("cam1", "Yeni ad", null, null, 1, CancellationToken.None);
        Assert.True(renamed.Succeeded);
        Assert.Equal(("Yeni ad", "rtsp://admin:gizli@h/1", true, 2), (renamed.Camera!.Name, renamed.Camera.RtspUrl, renamed.Camera.Enabled, renamed.Camera.Version));
        Assert.True(renamed.Camera.UpdatedAt >= renamed.Camera.CreatedAt);

        // Eski sürümle yazma reddedilir ve güncel satır döner.
        var stale = await _repository.UpdateAsync("cam1", "Başka", new Uri("rtsp://h/x"), false, 1, CancellationToken.None);
        Assert.Equal(CameraWriteStatus.VersionMismatch, stale.Status);
        Assert.Equal(2, stale.Camera!.Version);
        Assert.Equal("Yeni ad", (await _repository.FindAsync("cam1", CancellationToken.None))!.Name);

        // Sürüm verilmezse koşulsuz yazılır.
        var forced = await _repository.UpdateAsync("cam1", "Zorla", new Uri("rtsp://h/2"), false, null, CancellationToken.None);
        Assert.Equal(("rtsp://h/2", false, 3), (forced.Camera!.RtspUrl, forced.Camera.Enabled, forced.Camera.Version));

        Assert.Equal(CameraWriteStatus.NotFound, (await _repository.UpdateAsync("yok", "a", null, true, null, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Concurrent_updates_with_same_version_only_one_wins()
    {
        await _repository.InsertAsync(Cam("cam1", "rtsp://h/1"), CancellationToken.None);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            _repository.UpdateAsync("cam1", $"Ad {i}", null, true, 1, CancellationToken.None)));

        Assert.Single(results, r => r.Succeeded);
        Assert.Equal(7, results.Count(r => r.Status == CameraWriteStatus.VersionMismatch));
        Assert.Equal(2, (await _repository.FindAsync("cam1", CancellationToken.None))!.Version);
    }

    [Fact]
    public async Task Delete_honours_expected_version()
    {
        await _repository.InsertAsync(Cam("cam1", "rtsp://h/1"), CancellationToken.None);

        Assert.Equal(CameraWriteStatus.VersionMismatch, (await _repository.DeleteAsync("cam1", 5, CancellationToken.None)).Status);
        Assert.True((await _repository.DeleteAsync("cam1", 1, CancellationToken.None)).Succeeded);
        Assert.Equal(CameraWriteStatus.NotFound, (await _repository.DeleteAsync("cam1", null, CancellationToken.None)).Status);
        Assert.Empty(await _repository.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Seed_runs_once_even_when_instances_start_concurrently()
    {
        CameraDefinition[] seed = [Cam("cam1", "rtsp://h/1"), new() { Id = "cam2", Name = "", RtspUrl = new Uri("rtsp://h/2"), Enabled = false }];

        var counts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _repository.SeedIfEmptyAsync(seed, CancellationToken.None)));

        Assert.Equal([0, 0, 0, 2], counts.Order());
        var all = await _repository.ListAsync(CancellationToken.None);
        Assert.Equal(["cam1", "cam2"], all.Select(c => c.Id));
        Assert.Equal("cam2", all[1].Name);
        Assert.False(all[1].Enabled);

        // Tablo doluyken yapılandırma yok sayılır.
        Assert.Equal(0, await _repository.SeedIfEmptyAsync([Cam("cam9", "rtsp://h/9")], CancellationToken.None));
        Assert.Equal(2, (await _repository.ListAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Cameras_deleted_after_seeding_do_not_come_back()
    {
        await _repository.SeedIfEmptyAsync([Cam("cam1", "rtsp://h/1")], CancellationToken.None);
        await _repository.DeleteAsync("cam1", null, CancellationToken.None);

        Assert.Equal(0, await _repository.SeedIfEmptyAsync([Cam("cam1", "rtsp://h/1")], CancellationToken.None));
        Assert.Empty(await _repository.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Existing_database_is_marked_and_never_seeded()
    {
        // Önceki aşamalardan kalan (eski senkronizasyonla dolmuş) veritabanı.
        await _repository.InsertAsync(Cam("eski", "rtsp://h/eski"), CancellationToken.None);
        Assert.Equal(0, await _repository.SeedIfEmptyAsync([Cam("cam1", "rtsp://h/1")], CancellationToken.None));

        await _repository.DeleteAsync("eski", null, CancellationToken.None);
        Assert.Equal(0, await _repository.SeedIfEmptyAsync([Cam("cam1", "rtsp://h/1")], CancellationToken.None));
    }

    private static CameraDefinition Cam(string id, string url, bool enabled = true) =>
        new() { Id = id, Name = id.ToUpperInvariant(), RtspUrl = new Uri(url), Enabled = enabled };
}
