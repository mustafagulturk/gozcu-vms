using Gozcu.AnalysisService;
using Gozcu.AnalysisService.Events;
using Gozcu.AnalysisService.Pipeline;
using Gozcu.Media.Ffmpeg;
using Gozcu.Media.Frames;
using Gozcu.Media.Processes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Gozcu.UnitTests.Analysis;

public sealed class CameraAnalyzerTests : IDisposable
{
    private static readonly StreamInfo Hd = new("h264", 1280, 720, 25);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private readonly DetectionQueue _queue = new();
    private readonly InferenceGate _gate = new(2);
    private readonly CancellationTokenSource _cts = new();

    [Fact]
    public async Task Analyzed_frames_with_detections_and_event_transitions_reach_the_writer_queue()
    {
        using var detector = new FakeDetector(_ => [FakeDetector.Person()]);
        var source = new FakeFrameSource(async (handler, ct) =>
        {
            handler.OnStreamInfo(Hd);
            for (byte i = 1; i <= 3; i++)
            {
                handler.OnFrame(FakeDetector.Frame(i));
                await detector.WaitForCallsAsync(i);
                _time.Advance(TimeSpan.FromMilliseconds(500));
            }

            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        });
        var analyzer = NewAnalyzer(source, detector);

        var run = analyzer.RunAsync(_cts.Token);
        await FakeDetector.Until(() => analyzer.Status().FramesAnalyzed == 3, "3 kare analiz edilmeli");

        var status = analyzer.Status();
        Assert.Equal(AnalyzerState.Analyzing, status.State);
        Assert.Equal(Hd, status.Source);
        Assert.Equal(1, status.LastFrameDetections);
        Assert.Single(status.OpenEvents);
        Assert.Equal(0, status.FramesDropped);

        var latest = analyzer.Latest!;
        Assert.Equal(3, latest.Frame[0]);
        Assert.Equal(1280, latest.Result.Geometry.SourceWidth);
        Assert.Equal(720, latest.Result.Geometry.SourceHeight);

        _cts.Cancel();
        await run;
        Assert.Equal(AnalyzerState.Stopped, analyzer.Status().State);

        var outputs = Drain();
        Assert.Equal(4, outputs.Count);
        Assert.All(outputs.Take(3), o => Assert.Equal("cam1", o.Frame!.CameraId));
        Assert.Equal(
            [_time.Start, _time.Start.AddMilliseconds(500), _time.Start.AddSeconds(1)],
            outputs.Take(3).Select(o => o.Frame!.CapturedAt));
        Assert.Empty(outputs[0].Transitions);
        Assert.Equal(DetectionEventChange.Started, Assert.Single(outputs[1].Transitions).Change);
        Assert.Equal(DetectionEventChange.Updated, Assert.Single(outputs[2].Transitions).Change);

        // Durdurulunca açık olay son görüldüğü anla kapanır.
        Assert.Null(outputs[3].Frame);
        var ended = Assert.Single(outputs[3].Transitions);
        Assert.Equal(DetectionEventChange.Ended, ended.Change);
        Assert.Equal(_time.Start.AddSeconds(1), ended.Event.EndedAt);
        Assert.Equal(_time.Start, ended.Event.StartedAt);
    }

    [Fact]
    public async Task Frames_without_detections_are_not_sent_to_the_writer()
    {
        using var detector = new FakeDetector();
        var source = new FakeFrameSource(async (handler, ct) =>
        {
            handler.OnFrame(FakeDetector.Frame(1));
            await detector.WaitForCallsAsync(1);
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        });
        var analyzer = NewAnalyzer(source, detector);

        var run = analyzer.RunAsync(_cts.Token);
        await FakeDetector.Until(() => analyzer.Status().FramesAnalyzed == 1, "kare analiz edilmeli");
        _cts.Cancel();
        await run;

        Assert.Empty(Drain());
        Assert.Equal(0, analyzer.Status().LastFrameDetections);

        // Akış bilgisi gelmeden okunan kare model boyutunda kabul edilir.
        Assert.Equal(FakeDetector.Size, analyzer.Latest!.Result.Geometry.SourceWidth);
    }

    [Fact]
    public async Task Slow_inference_drops_oldest_frames_and_analyzes_the_freshest()
    {
        using var detector = new FakeDetector(blockFirstCall: true);
        var source = new FakeFrameSource(async (handler, ct) =>
        {
            handler.OnFrame(FakeDetector.Frame(1));
            await detector.WaitForCallsAsync(1);

            // Çıkarım 1. karede takılıyken 4 kare daha gelir; kanalda yalnızca en yenisi kalır.
            for (byte i = 2; i <= 5; i++)
            {
                handler.OnFrame(FakeDetector.Frame(i));
            }

            detector.Release();
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        });
        var analyzer = NewAnalyzer(source, detector);

        var run = analyzer.RunAsync(_cts.Token);
        await detector.WaitForCallsAsync(2);
        await FakeDetector.Until(() => analyzer.Status().FramesAnalyzed == 2, "2 kare analiz edilmeli");
        await Task.Delay(100);

        Assert.Equal([1, 5], detector.Frames);
        var status = analyzer.Status();
        Assert.Equal(5, status.FramesReceived);
        Assert.Equal(3, status.FramesDropped);
        Assert.Equal(2, status.FramesAnalyzed);

        _cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Reconnects_after_source_ends_and_closes_open_events_on_stream_loss()
    {
        using var detector = new FakeDetector(_ => [FakeDetector.Person()]);
        var source = new FakeFrameSource(
            async (handler, ct) =>
            {
                handler.OnStreamInfo(Hd);
                handler.OnFrame(FakeDetector.Frame(1));
                await detector.WaitForCallsAsync(1);
                handler.OnErrorLine("Connection reset by peer");
                return 1;
            },
            async (handler, ct) =>
            {
                handler.OnFrame(FakeDetector.Frame(2));
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            });
        var events = new DetectionEventOptions { MinConsecutiveFrames = 1, MinConfidence = 0.5f, EndAfter = TimeSpan.FromSeconds(5) };
        var analyzer = NewAnalyzer(source, detector, events);

        var run = analyzer.RunAsync(_cts.Token);
        await FakeDetector.Until(() => analyzer.Status().State == AnalyzerState.Reconnecting, "yeniden bağlanma beklenmeli");

        var waiting = analyzer.Status();
        Assert.Equal(_time.GetUtcNow().AddSeconds(1), waiting.NextRetryAt);
        Assert.Equal("Connection reset by peer", waiting.LastError);
        Assert.Empty(waiting.OpenEvents);

        // Akış koptuğunda açık olay kapanır: Started (1. kare) ve Ended (kopma).
        var outputs = Drain();
        Assert.Equal(
            [DetectionEventChange.Started, DetectionEventChange.Ended],
            outputs.SelectMany(o => o.Transitions).Select(t => t.Change));

        _time.Advance(TimeSpan.FromSeconds(1));
        await FakeDetector.Until(() => analyzer.Status().FramesAnalyzed == 2, "yeniden bağlandıktan sonra kare analiz edilmeli");

        var status = analyzer.Status();
        Assert.Equal(2, source.Runs);
        Assert.Equal(AnalyzerState.Analyzing, status.State);
        Assert.Equal(1, status.ReconnectCount);
        Assert.Null(status.LastError);
        Assert.Null(status.NextRetryAt);

        _cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Start_failures_back_off_exponentially()
    {
        using var detector = new FakeDetector();
        var source = new FakeFrameSource(
            (_, _) => throw new InvalidOperationException("ffmpeg bulunamadı"),
            (_, _) => throw new InvalidOperationException("ffmpeg bulunamadı"));
        var analyzer = NewAnalyzer(source, detector);

        var run = analyzer.RunAsync(_cts.Token);
        await FakeDetector.Until(() => analyzer.Status().State == AnalyzerState.Reconnecting, "ilk deneme başarısız olmalı");
        Assert.Equal("ffmpeg bulunamadı", analyzer.Status().LastError);
        Assert.Equal(_time.GetUtcNow().AddSeconds(1), analyzer.Status().NextRetryAt);

        _time.Advance(TimeSpan.FromSeconds(1));
        await FakeDetector.Until(() => source.Runs == 2 && analyzer.Status().NextRetryAt == _time.GetUtcNow().AddSeconds(2), "ikinci gecikme 2 sn olmalı");

        _time.Advance(TimeSpan.FromSeconds(2));
        await FakeDetector.Until(() => analyzer.Status().State == AnalyzerState.Connecting, "üçüncü deneme başlamalı");

        _cts.Cancel();
        await run;
        Assert.Equal(AnalyzerState.Stopped, analyzer.Status().State);
    }

    [Fact]
    public async Task Inference_gate_limits_concurrency_across_cameras()
    {
        using var gate = new InferenceGate(1);
        using var detector = new FakeDetector(work: TimeSpan.FromMilliseconds(30));
        Func<IFrameSourceHandler, CancellationToken, Task<int>> script = async (handler, ct) =>
        {
            for (byte i = 1; i <= 5; i++)
            {
                handler.OnFrame(FakeDetector.Frame(i));
                await Task.Delay(5, ct);
            }

            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        };
        var analyzers = Enumerable.Range(1, 3)
            .Select(i => NewAnalyzer(new FakeFrameSource(script), detector, cameraId: $"cam{i}", gate: gate))
            .ToList();

        var runs = analyzers.Select(a => a.RunAsync(_cts.Token)).ToList();
        await FakeDetector.Until(() => analyzers.All(a => a.Status().FramesAnalyzed >= 2), "her kamera kare analiz etmeli");
        _cts.Cancel();
        await Task.WhenAll(runs);

        Assert.Equal(1, detector.MaxConcurrent);
    }

    public void Dispose()
    {
        _cts.Dispose();
        _gate.Dispose();
    }

    private CameraAnalyzer NewAnalyzer(
        IFrameSource source,
        FakeDetector detector,
        DetectionEventOptions? events = null,
        string cameraId = "cam1",
        InferenceGate? gate = null)
    {
        var settings = new CameraAnalyzerSettings(
            cameraId,
            "Test",
            new FrameSourceRequest(new ProcessSpec("ffmpeg", []), FakeDetector.Size, FakeDetector.Size),
            new ReconnectPolicy { InitialDelay = TimeSpan.FromSeconds(1), JitterRatio = 0 },
            events ?? new DetectionEventOptions());
        return new CameraAnalyzer(settings, source, detector, gate ?? _gate, _queue, _time, NullLogger.Instance);
    }

    private List<AnalysisOutput> Drain()
    {
        var outputs = new List<AnalysisOutput>();
        while (_queue.Reader.TryRead(out var output))
        {
            outputs.Add(output);
        }

        return outputs;
    }
}
