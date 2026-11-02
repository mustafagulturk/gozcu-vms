using Gozcu.AnalysisService;
using Gozcu.AnalysisService.Persistence;
using Gozcu.AnalysisService.Pipeline;
using Gozcu.AnalysisService.Snapshots;
using Gozcu.Contracts.Analysis;
using Gozcu.Contracts.Events;
using Gozcu.Infrastructure.Messaging;
using Gozcu.Infrastructure.Persistence;
using Gozcu.IntegrationTests.Infrastructure;
using Gozcu.Media.Ffmpeg;
using Gozcu.Media.Frames;
using Gozcu.Media.Processes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gozcu.IntegrationTests.Analysis;

/// <summary>
/// Uçtan uca: sahte kamera (testsrc 640x360 @ 25 fps) → MediaMTX → gerçek ffmpeg çözücü (FfmpegFrameSource) →
/// sahte tespit edici → olay takibi → PostgreSQL + RabbitMQ.
/// </summary>
[Collection(MediaMtxCollection.Name)]
public sealed class AnalysisPipelineIntegrationTests(MediaMtxFixture mediaMtx, AnalysisBackendFixture backend) : IClassFixture<AnalysisBackendFixture>
{
    private const int ModelSize = 320;
    private const double Fps = 4;

    [Fact]
    public async Task Rtsp_stream_is_decoded_to_letterboxed_frames_at_configured_rate_and_detections_reach_db_and_broker()
    {
        const string path = "analysis-e2e";
        await using var publisher = mediaMtx.CreatePublisher(path);
        publisher.Start();

        await using var subscriber = await backend.SubscribeAsync();
        await using var connection = new RabbitMqConnection(backend.RabbitOptions, NullLogger<RabbitMqConnection>.Instance);
        await using var eventPublisher = new RabbitMqEventPublisher(connection, backend.RabbitOptions);
        var repository = new DetectionRepository(backend.DataSource);
        var queue = new DetectionQueue();
        var metrics = new DetectionWriterMetrics();
        using var writer = new DetectionWriter(
            queue, backend.Readiness, repository, eventPublisher, metrics, TimeProvider.System, NullLogger<DetectionWriter>.Instance);
        await writer.StartAsync(CancellationToken.None);

        var detector = new FixedDetector(ModelSize);
        using var gate = new InferenceGate(1);
        var spec = new ProcessSpec("ffmpeg", FfmpegCommands.AnalysisFrames(mediaMtx.CameraUrl(path), Fps, ModelSize, ModelSize));
        var settings = new CameraAnalyzerSettings(
            path, "Uçtan uca", new FrameSourceRequest(spec, ModelSize, ModelSize),
            new ReconnectPolicy { InitialDelay = TimeSpan.FromMilliseconds(500), JitterRatio = 0 },
            new DetectionEventOptions());
        var analyzer = new CameraAnalyzer(settings, new FfmpegFrameSource(), detector, gate, queue, TimeProvider.System, NullLogger.Instance);
        using var cts = new CancellationTokenSource();
        var run = analyzer.RunAsync(cts.Token);

        // Yayıncı MediaMTX'e bağlanana kadar ilk denemeler başarısız olabilir; analizci yeniden bağlanır.
        await Eventually.TrueAsync(() => analyzer.Status().FramesAnalyzed >= 1, TimeSpan.FromSeconds(40), "ilk kare analiz edilmeli");
        var firstReceived = analyzer.Status().FramesReceived;
        await Task.Delay(TimeSpan.FromSeconds(5));
        var received = analyzer.Status().FramesReceived - firstReceived;

        var status = analyzer.Status();
        Assert.Equal(AnalyzerState.Analyzing, status.State);
        Assert.Equal(new StreamInfo("h264", 640, 360, 25), status.Source);
        Assert.InRange(received, (long)(5 * Fps * 0.6), (long)(5 * Fps * 1.4));
        Assert.InRange(status.AnalysisFps, Fps * 0.6, Fps * 1.4);
        Assert.Single(status.OpenEvents);

        // Kareler model boyutunda ve letterbox'lı: 640x360 → 320x180 görüntü + alt kısımda gri (114) dolgu.
        var calls = detector.Calls;
        Assert.All(calls, c => Assert.Equal(ModelSize * ModelSize * 3, c.Length));
        Assert.All(calls, c => Assert.Equal(new LetterboxGeometryDto(640, 360, 320, 180), Dto(c.Geometry)));
        Assert.All(calls, c => Assert.InRange(c.PaddingSample, 112, 116));

        var latest = analyzer.Latest!;
        var bmp = SnapshotRenderer.RenderBmp(latest.Frame, latest.Result.Geometry, latest.Result.Detections);
        Assert.Equal(BmpWriter.HeaderSize + (BmpWriter.RowStride(320) * 180), bmp.Length);

        var started = await subscriber.WaitForAsync(1, TimeSpan.FromSeconds(10));
        Assert.Equal(EventRoutingKeys.DetectionEventStarted, started[0].RoutingKey);
        var startedEvent = EventSerializer.Deserialize<DetectionEventStarted>(started[0].Body.Span)!;
        Assert.Equal((path, ObjectCategory.Person, 1), (startedEvent.CameraId, startedEvent.Category, startedEvent.ObjectCount));

        // Durdurma: açık olay kapanır, yazıcı kalanları yazar ve "bitti" olayını yayınlar.
        cts.Cancel();
        await run;
        await writer.StopAsync(CancellationToken.None);

        var all = await subscriber.WaitForAsync(2, TimeSpan.FromSeconds(10));
        Assert.Equal(EventRoutingKeys.DetectionEventEnded, all[1].RoutingKey);
        var endedEvent = EventSerializer.Deserialize<DetectionEventEnded>(all[1].Body.Span)!;
        Assert.Equal(startedEvent.DetectionEventId, endedEvent.DetectionEventId);
        Assert.Equal(analyzer.Status().FramesAnalyzed, endedEvent.FrameCount);

        var frames = await repository.FindFramesAsync(path, null, null, 1000, CancellationToken.None);
        Assert.Equal(analyzer.Status().FramesAnalyzed, frames.Count);
        Assert.Equal("person", frames[0].Detections.Single().Label);

        var stored = Assert.Single(await repository.FindEventsAsync(path, ObjectCategory.Person, null, null, 10, CancellationToken.None));
        Assert.Equal(startedEvent.DetectionEventId, stored.Id);
        Assert.Equal(frames[0].CapturedAt, stored.EndedAt);
        Assert.Equal(frames[^1].CapturedAt, stored.StartedAt);
    }

    private static LetterboxGeometryDto Dto(Gozcu.Vision.LetterboxGeometry g) => new(g.SourceWidth, g.SourceHeight, g.ContentWidth, g.ContentHeight);

    private sealed record LetterboxGeometryDto(int SourceWidth, int SourceHeight, int ContentWidth, int ContentHeight);
}
