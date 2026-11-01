using System.Collections.Concurrent;
using Gozcu.Contracts.Cameras;
using Gozcu.Infrastructure.Cameras;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gozcu.UnitTests.Cameras;

public sealed class CameraWorkerHostTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Starts_enabled_cameras_and_skips_disabled_or_invalid_ones()
    {
        var source = new CatalogSource(Cam("a"), Cam("b", enabled: false), new CameraDefinition { Id = "c", Name = "C" });
        await using var fixture = await HostFixture.StartAsync(source);

        await Eventually(() => fixture.Host.Running.Keys.Order().SequenceEqual(["a"]));
        Assert.Equal(["a"], fixture.Host.RunningCameras.Select(c => c.Id));
        Assert.Equal(1, fixture.Host.StartCount("a"));
    }

    [Fact]
    public async Task Added_camera_is_started_and_deleted_or_disabled_camera_is_stopped()
    {
        var source = new CatalogSource(Cam("a"), Cam("b"));
        await using var fixture = await HostFixture.StartAsync(source);
        await Eventually(() => fixture.Host.Running.Count == 2);

        await fixture.ChangeAsync(Cam("a", enabled: false), Cam("c"));

        await Eventually(() => fixture.Host.Running.Keys.Order().SequenceEqual(["c"]));
        Assert.Equal(1, fixture.Host.StopCount("a"));
        Assert.Equal(1, fixture.Host.StopCount("b"));
        Assert.Equal(1, fixture.Host.StartCount("c"));
    }

    [Fact]
    public async Task Changed_url_or_name_restarts_only_that_camera()
    {
        var source = new CatalogSource(Cam("a"), Cam("b"), Cam("c"));
        await using var fixture = await HostFixture.StartAsync(source);
        await Eventually(() => fixture.Host.Running.Count == 3);

        await fixture.ChangeAsync(Cam("a", url: "rtsp://yeni/a"), Cam("b", name: "Yeni ad"), Cam("c"));

        await Eventually(() => fixture.Host.StartCount("a") == 2 && fixture.Host.StartCount("b") == 2);
        Assert.Equal("rtsp://yeni/a", fixture.Host.Running["a"].RtspUrl!.OriginalString);
        Assert.Equal("Yeni ad", fixture.Host.Running["b"].Name);
        Assert.Equal(1, fixture.Host.StartCount("c"));
        Assert.Equal(0, fixture.Host.StopCount("c"));
    }

    [Fact]
    public async Task Restart_waits_for_the_old_worker_to_finish_before_starting_the_new_one()
    {
        var source = new CatalogSource(Cam("a"));
        await using var fixture = await HostFixture.StartAsync(source);
        await Eventually(() => fixture.Host.Running.ContainsKey("a"));

        await fixture.ChangeAsync(Cam("a", url: "rtsp://yeni/a"));
        await Eventually(() => fixture.Host.StartCount("a") == 2);

        // İki iş hiçbir an aynı anda çalışmadı (ör. aynı dizine iki kayıt süreci yazmaz).
        Assert.Equal(1, fixture.Host.MaxConcurrent("a"));
    }

    [Fact]
    public async Task Unchanged_catalog_refresh_does_not_restart_workers()
    {
        var source = new CatalogSource(Cam("a"));
        await using var fixture = await HostFixture.StartAsync(source);
        await Eventually(() => fixture.Host.Running.ContainsKey("a"));

        var version = fixture.Catalog.Version;
        await fixture.ChangeAsync(Cam("a"));

        Assert.Equal(version, fixture.Catalog.Version);
        await Task.Delay(100);
        Assert.Equal(1, fixture.Host.StartCount("a"));
    }

    [Fact]
    public async Task Crashed_worker_is_restarted()
    {
        var source = new CatalogSource(Cam("a"));
        await using var fixture = await HostFixture.StartAsync(source);
        await Eventually(() => fixture.Host.Running.ContainsKey("a"));

        fixture.Host.Crash("a");

        await Eventually(() => fixture.Host.StartCount("a") == 2 && fixture.Host.Running.ContainsKey("a"));
    }

    [Fact]
    public async Task Stopping_the_host_stops_all_workers()
    {
        var source = new CatalogSource(Cam("a"), Cam("b"));
        var fixture = await HostFixture.StartAsync(source);
        await Eventually(() => fixture.Host.Running.Count == 2);

        await fixture.DisposeAsync();

        Assert.Empty(fixture.Host.Running);
        Assert.Equal(1, fixture.Host.StopCount("a"));
        Assert.Equal(1, fixture.Host.StopCount("b"));
    }

    [Fact]
    public async Task Workers_do_not_start_before_the_catalog_is_loaded()
    {
        var source = new CatalogSource(Cam("a"));
        var catalog = new CameraCatalog(source.LoadAsync, NullLogger.Instance);
        var host = new RecordingHost(catalog);
        await host.StartAsync(CancellationToken.None);

        await Task.Delay(100);
        Assert.Empty(host.Running);

        await catalog.RefreshAsync(CancellationToken.None);
        await Eventually(() => host.Running.ContainsKey("a"));
        await host.StopAsync(CancellationToken.None);
    }

    private static CameraDefinition Cam(string id, bool enabled = true, string? name = null, string? url = null) => new()
    {
        Id = id,
        Name = name ?? id.ToUpperInvariant(),
        RtspUrl = new Uri(url ?? $"rtsp://kamera/{id}"),
        Enabled = enabled,
    };

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "koşul zamanında gerçekleşmedi");
            await Task.Delay(10);
        }
    }

    private sealed class CatalogSource(params CameraDefinition[] cameras)
    {
        public IReadOnlyList<CameraDefinition> Cameras { get; set; } = cameras;

        public Task<IReadOnlyList<CameraDefinition>> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Cameras);
    }

    private sealed class HostFixture : IAsyncDisposable
    {
        private readonly CatalogSource _source;
        private bool _disposed;

        private HostFixture(CatalogSource source, CameraCatalog catalog, RecordingHost host)
        {
            _source = source;
            Catalog = catalog;
            Host = host;
        }

        public CameraCatalog Catalog { get; }

        public RecordingHost Host { get; }

        public static async Task<HostFixture> StartAsync(CatalogSource source)
        {
            var catalog = new CameraCatalog(source.LoadAsync, NullLogger.Instance);
            await catalog.RefreshAsync(CancellationToken.None);
            var host = new RecordingHost(catalog);
            await host.StartAsync(CancellationToken.None);
            return new HostFixture(source, catalog, host);
        }

        public Task ChangeAsync(params CameraDefinition[] cameras)
        {
            _source.Cameras = cameras;
            return Catalog.RefreshAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await Host.StopAsync(CancellationToken.None);
            Host.Dispose();
            Catalog.Dispose();
        }
    }

    /// <summary>Başlatma/durdurma sayılarını ve eşzamanlılığı kaydeden test işçisi.</summary>
    private sealed class RecordingHost(ICameraCatalog catalog) : CameraWorkerHost(catalog, NullLogger.Instance)
    {
        private readonly ConcurrentDictionary<string, int> _starts = new();
        private readonly ConcurrentDictionary<string, int> _stops = new();
        private readonly ConcurrentDictionary<string, int> _concurrent = new();
        private readonly ConcurrentDictionary<string, int> _maxConcurrent = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _crash = new();

        public ConcurrentDictionary<string, CameraDefinition> Running { get; } = new();

        protected override string WorkerName => "test";

        protected override TimeSpan WorkerRestartDelay => TimeSpan.FromMilliseconds(20);

        public int StartCount(string id) => _starts.GetValueOrDefault(id);

        public int StopCount(string id) => _stops.GetValueOrDefault(id);

        public int MaxConcurrent(string id) => _maxConcurrent.GetValueOrDefault(id);

        public void Crash(string id) => _crash[id].TrySetException(new InvalidOperationException("çöktü"));

        protected override async Task RunCameraAsync(CameraDefinition camera, CancellationToken cancellationToken)
        {
            var crash = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _crash[camera.Id] = crash;
            var now = _concurrent.AddOrUpdate(camera.Id, 1, (_, v) => v + 1);
            _maxConcurrent.AddOrUpdate(camera.Id, now, (_, v) => Math.Max(v, now));
            Running[camera.Id] = camera;
            _starts.AddOrUpdate(camera.Id, 1, (_, v) => v + 1);
            try
            {
                await Task.WhenAny(Task.Delay(System.Threading.Timeout.Infinite, cancellationToken), crash.Task);
                await crash.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                // Yavaş kapanan bir süreci canlandırır.
                await Task.Delay(30, CancellationToken.None);
                Running.TryRemove(camera.Id, out _);
                _concurrent.AddOrUpdate(camera.Id, 0, (_, v) => v - 1);
                if (cancellationToken.IsCancellationRequested)
                {
                    _stops.AddOrUpdate(camera.Id, 1, (_, v) => v + 1);
                }
            }
        }
    }
}
