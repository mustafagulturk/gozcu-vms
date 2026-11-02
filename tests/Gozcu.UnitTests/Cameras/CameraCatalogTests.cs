using Gozcu.Contracts.Cameras;
using Gozcu.Infrastructure.Cameras;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gozcu.UnitTests.Cameras;

public sealed class CameraCatalogTests
{
    [Fact]
    public async Task First_load_completes_waiters_and_sorts_by_id()
    {
        using var catalog = new CameraCatalog(_ => Load(Cam("b"), Cam("a")), NullLogger.Instance);
        var loaded = catalog.WaitUntilLoadedAsync(CancellationToken.None);
        Assert.False(loaded.IsCompleted);
        Assert.Equal(0, catalog.Version);

        await catalog.RefreshAsync(CancellationToken.None);

        await loaded;
        Assert.Equal(1, catalog.Version);
        Assert.Equal(["a", "b"], catalog.Cameras.Select(c => c.Id));
    }

    [Fact]
    public async Task Empty_first_load_still_counts_as_loaded()
    {
        using var catalog = new CameraCatalog(_ => Load(), NullLogger.Instance);
        await catalog.RefreshAsync(CancellationToken.None);

        await catalog.WaitUntilLoadedAsync(CancellationToken.None);
        Assert.Equal(1, catalog.Version);
        Assert.Empty(catalog.Cameras);
    }

    [Fact]
    public async Task Version_increases_only_when_content_changes()
    {
        var cameras = new[] { Cam("a") };
        using var catalog = new CameraCatalog(_ => Load(cameras), NullLogger.Instance);
        await catalog.RefreshAsync(CancellationToken.None);
        await catalog.RefreshAsync(CancellationToken.None);
        Assert.Equal(1, catalog.Version);

        var waiter = catalog.WaitForChangeAsync(1, CancellationToken.None);
        Assert.False(waiter.IsCompleted);

        cameras = [Cam("a", enabled: false)];
        await catalog.RefreshAsync(CancellationToken.None);

        Assert.Equal(2, await waiter);
        Assert.False(catalog.Find("a")!.Enabled);
    }

    [Fact]
    public async Task Wait_for_change_returns_immediately_for_stale_version()
    {
        using var catalog = new CameraCatalog(_ => Load(Cam("a")), NullLogger.Instance);
        await catalog.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, await catalog.WaitForChangeAsync(0, CancellationToken.None));
    }

    [Fact]
    public async Task Find_is_case_insensitive_and_catalog_is_isolated_from_source_objects()
    {
        var source = Cam("Cam1");
        using var catalog = new CameraCatalog(_ => Load(source), NullLogger.Instance);
        await catalog.RefreshAsync(CancellationToken.None);

        source.Name = "değiştirildi";

        Assert.Equal("CAM1", catalog.Find("cam1")!.Name);
        Assert.Null(catalog.Find("yok"));
    }

    [Fact]
    public async Task Failed_refresh_keeps_last_known_cameras()
    {
        var fail = false;
        using var catalog = new CameraCatalog(
            _ => fail ? throw new InvalidOperationException("veritabanı yok") : Load(Cam("a")),
            NullLogger.Instance);
        await catalog.RefreshAsync(CancellationToken.None);

        fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.RefreshAsync(CancellationToken.None));

        Assert.Equal(["a"], catalog.Cameras.Select(c => c.Id));
        Assert.Equal(1, catalog.Version);
    }

    private static Task<IReadOnlyList<CameraDefinition>> Load(params CameraDefinition[] cameras) =>
        Task.FromResult<IReadOnlyList<CameraDefinition>>(cameras);

    private static CameraDefinition Cam(string id, bool enabled = true) =>
        new() { Id = id, Name = id.ToUpperInvariant(), RtspUrl = new Uri($"rtsp://h/{id}"), Enabled = enabled };
}
