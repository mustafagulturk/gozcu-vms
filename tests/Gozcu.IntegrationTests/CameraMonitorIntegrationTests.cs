using Gozcu.Contracts.Cameras;
using Gozcu.IntegrationTests.Infrastructure;
using Gozcu.Media.Ffmpeg;
using Gozcu.Media.Processes;
using Gozcu.StreamingService;
using Gozcu.StreamingService.Health;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gozcu.IntegrationTests;

[Collection(MediaMtxCollection.Name)]
public sealed class CameraMonitorIntegrationTests(MediaMtxFixture mediaMtx)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Camera_goes_online_detects_disconnect_and_reconnects_automatically()
    {
        await using var publisher = mediaMtx.CreatePublisher("monitor-test");
        var camera = new CameraDefinition { Id = "monitor-test", Name = "Test", RtspUrl = mediaMtx.CameraUrl("monitor-test") };
        var tracker = new CameraHealthTracker(camera, new StreamingOptions(), TimeProvider.System);
        var transitions = new List<CameraStateTransition>();
        var monitor = new CameraMonitor(tracker, t => { lock (transitions) { transitions.Add(t); } });
        var supervisor = new ProcessSupervisor(
            new ProcessRunner(),
            new ReconnectPolicy { InitialDelay = TimeSpan.FromMilliseconds(500), MaxDelay = TimeSpan.FromSeconds(2) },
            TimeProvider.System,
            NullLogger.Instance);
        var spec = new ProcessSpec("ffmpeg", FfmpegCommands.Monitor(camera.RtspUrl!, TimeSpan.FromSeconds(3))) { GracefulStopInput = "q" };
        using var cts = new CancellationTokenSource();

        // Kamera henüz yayında değilken izleme başlar: bağlantı reddedilir, yeniden denenir.
        var run = supervisor.RunAsync(() => spec, monitor, cts.Token);
        await Eventually.TrueAsync(() => tracker.State == CameraHealthState.Offline, Timeout, "yayıncı yokken kamera çevrimdışı olmalı");

        publisher.Start();
        await Eventually.TrueAsync(() => tracker.State == CameraHealthState.Online, Timeout, "yayın başlayınca kamera çevrimiçi olmalı");
        var snapshot = tracker.Snapshot();
        Assert.Equal("h264", snapshot.Stream?.Codec);
        Assert.Equal(640, snapshot.Stream?.Width);

        // Kamera kopar.
        await publisher.StopAsync();
        await Eventually.TrueAsync(() => tracker.State is CameraHealthState.Offline or CameraHealthState.Degraded, Timeout, "kopma algılanmalı");

        // Kamera geri gelir; servis müdahalesiz yeniden bağlanmalı.
        publisher.Start();
        await Eventually.TrueAsync(() => tracker.State == CameraHealthState.Online, Timeout, "kamera geri gelince yeniden bağlanmalı");
        Assert.True(tracker.Snapshot().ReconnectCount >= 1);

        cts.Cancel();
        await run;

        lock (transitions)
        {
            Assert.Contains(transitions, t => t.Previous == CameraHealthState.Online);
        }
    }
}
