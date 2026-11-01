using Gozcu.Contracts.Cameras;
using Gozcu.Infrastructure.Cameras;
using Gozcu.IntegrationTests.Infrastructure;
using Gozcu.Media.Processes;
using Gozcu.RecordingService;
using Gozcu.RecordingService.Indexing;
using Gozcu.RecordingService.Recording;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gozcu.IntegrationTests.Cameras;

/// <summary>Kayıt servisi katalog değişikliğine göre gerçek ffmpeg kayıt süreçlerini başlatır ve nazikçe durdurur.</summary>
[Collection(MediaMtxCollection.Name)]
public sealed class DynamicRecorderIntegrationTests(MediaMtxFixture mediaMtx) : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(40);
    private readonly string _root = Directory.CreateTempSubdirectory("gozcu-dyn-").FullName;

    [Fact]
    public async Task Camera_added_to_catalog_is_recorded_and_deleted_camera_is_stopped_gracefully()
    {
        const string id = "dyn-rec";
        await using var publisher = mediaMtx.CreatePublisher(id);
        publisher.Start();

        IReadOnlyList<CameraDefinition> cameras = [];
        using var catalog = new CameraCatalog(_ => Task.FromResult(cameras), NullLogger.Instance);
        await catalog.RefreshAsync(CancellationToken.None);

        var queue = new SegmentQueue();
        var registry = new RecorderRegistry();
        var options = Options.Create(new RecordingOptions
        {
            RootPath = _root,
            SegmentSeconds = 2,
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(500),
        });
        using var service = new RecorderService(catalog, options, new ProcessRunner(), queue, registry, TimeProvider.System, NullLoggerFactory.Instance);
        await service.StartAsync(CancellationToken.None);
        Assert.Empty(registry.Statuses());

        // Kamera eklenir: kayıt başlar ve parça üretir.
        cameras = [new CameraDefinition { Id = id, Name = "Dinamik", RtspUrl = mediaMtx.CameraUrl(id) }];
        await catalog.RefreshAsync(CancellationToken.None);
        var segments = new List<CompletedSegment>();
        await Eventually.TrueAsync(
            () =>
            {
                while (queue.Reader.TryRead(out var segment))
                {
                    segments.Add(segment);
                }

                return segments.Count >= 2;
            },
            Timeout,
            "eklenen kamera kaydedilmeli");
        Assert.Equal(RecorderState.Recording, Assert.Single(registry.Statuses()).State);

        // Yalnızca ad değişimi kaydı kesmez: aynı kaydedici çalışmaya devam eder (sayaçlar sıfırlanmaz).
        var before = Assert.Single(registry.Statuses());
        cameras = [new CameraDefinition { Id = id, Name = "Yeni ad", RtspUrl = mediaMtx.CameraUrl(id) }];
        await catalog.RefreshAsync(CancellationToken.None);
        await Task.Delay(500);
        var after = Assert.Single(registry.Statuses());
        Assert.Equal(before.RestartCount, after.RestartCount);
        Assert.True(after.SegmentsWritten >= before.SegmentsWritten);

        // Kamera silinir: süreç nazikçe durur, son parça kapanır, kayıt tablosundan çıkar; dosyalar diskte kalır.
        cameras = [];
        await catalog.RefreshAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => !registry.Statuses().Any(), Timeout, "silinen kameranın kaydı durmalı");
        while (queue.Reader.TryRead(out var segment))
        {
            segments.Add(segment);
        }

        Assert.All(segments, s => Assert.True(File.Exists(s.FilePath)));
        var last = segments[^1];
        Assert.Equal(["h264"], (await Ffprobe.ProbeAsync(last.FilePath)).VideoCodecs);
        Assert.Empty(service.RunningCameras);

        await service.StopAsync(CancellationToken.None);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
