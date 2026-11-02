using Gozcu.AnalysisService.Events;
using Gozcu.AnalysisService.Persistence;
using Gozcu.AnalysisService.Pipeline;
using Gozcu.Contracts.Analysis;
using Gozcu.Contracts.Events;
using Gozcu.Infrastructure.Messaging;
using Gozcu.Infrastructure.Persistence;
using Gozcu.IntegrationTests.Infrastructure;
using Gozcu.Vision;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gozcu.IntegrationTests.Analysis;

public sealed class DetectionWriterIntegrationTests(AnalysisBackendFixture backend) : IClassFixture<AnalysisBackendFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);
    private static readonly LetterboxGeometry Geometry = new(416, 416, 1280, 720);

    [Fact]
    public async Task Writer_stores_frames_and_events_and_publishes_started_and_ended()
    {
        var repository = new DetectionRepository(backend.DataSource);

        // Önceki çalıştırmadan açık kalmış olay: yazıcı başlarken kapatılmalı.
        var dangling = Guid.NewGuid();
        await repository.UpsertEventsAsync(
            [new DetectionEventRecord(dangling, "writer-cam", ObjectCategory.Vehicle, T0.AddHours(-1), null, 1, 0.7f, 2)], CancellationToken.None);

        await using var subscriber = await backend.SubscribeAsync();
        await using var connection = new RabbitMqConnection(backend.RabbitOptions, NullLogger<RabbitMqConnection>.Instance);
        await using var publisher = new RabbitMqEventPublisher(connection, backend.RabbitOptions);
        var queue = new DetectionQueue();
        var metrics = new DetectionWriterMetrics();
        using var writer = new DetectionWriter(
            queue, backend.Readiness, repository, publisher, metrics, TimeProvider.System, NullLogger<DetectionWriter>.Instance);

        var id = Guid.NewGuid();
        var started = State(id, at: 0.5, count: 2, max: 2, peak: 0.8f, frames: 2);
        queue.TryEnqueue(new AnalysisOutput(Frame(0), []));
        queue.TryEnqueue(new AnalysisOutput(Frame(0.5), [new(DetectionEventChange.Started, started)]));
        queue.TryEnqueue(new AnalysisOutput(Frame(1.0), [new(DetectionEventChange.Updated, State(id, at: 1.0, count: 3, max: 3, peak: 0.95f, frames: 3))]));
        // Boş kare saklanmaz.
        queue.TryEnqueue(new AnalysisOutput(new AnalyzedFrame("writer-cam", T0.AddSeconds(1.5), 5, Geometry, []), []));

        await writer.StartAsync(CancellationToken.None);
        var startMessages = await subscriber.WaitForAsync(1, TimeSpan.FromSeconds(15));
        var startMessage = Assert.Single(startMessages);
        Assert.Equal(EventRoutingKeys.DetectionEventStarted, startMessage.RoutingKey);
        Assert.Equal(nameof(DetectionEventStarted), startMessage.BasicProperties.Type);
        var startEvent = EventSerializer.Deserialize<DetectionEventStarted>(startMessage.Body.Span)!;
        Assert.Equal((id, "writer-cam", ObjectCategory.Person, T0, 2, 0.8f),
            (startEvent.DetectionEventId, startEvent.CameraId, startEvent.Category, startEvent.StartedAt, startEvent.ObjectCount, startEvent.Confidence));

        var ended = State(id, at: 1.0, count: 3, max: 3, peak: 0.95f, frames: 3) with { EndedAt = T0.AddSeconds(1) };
        queue.TryEnqueue(new AnalysisOutput(null, [new(DetectionEventChange.Ended, ended)]));

        var all = await subscriber.WaitForAsync(2, TimeSpan.FromSeconds(15));
        Assert.Equal(EventRoutingKeys.DetectionEventEnded, all[1].RoutingKey);
        var endEvent = EventSerializer.Deserialize<DetectionEventEnded>(all[1].Body.Span)!;
        Assert.Equal(
            new DetectionEventEnded(id, "writer-cam", ObjectCategory.Person, T0, T0.AddSeconds(1), 3, 0.95f, 3) { EventId = endEvent.EventId, OccurredAt = endEvent.OccurredAt },
            endEvent);

        await Eventually.TrueAsync(() => metrics.Snapshot(0).FramesWritten == 3, TimeSpan.FromSeconds(10), "3 kare yazılmalı");
        await writer.StopAsync(CancellationToken.None);

        var frames = await repository.FindFramesAsync("writer-cam", null, null, 10, CancellationToken.None);
        Assert.Equal([T0.AddSeconds(1), T0.AddSeconds(0.5), T0], frames.Select(f => f.CapturedAt));
        Assert.Equal(new StoredDetection("person", ObjectCategory.Person, 0.9f, 0.1f, 0.2f, 0.3f, 0.4f), frames[0].Detections.Single());

        var events = (await repository.FindEventsAsync("writer-cam", null, null, null, 10, CancellationToken.None)).ToDictionary(e => e.Id);
        Assert.Equal(new DetectionEventRecord(id, "writer-cam", ObjectCategory.Person, T0, T0.AddSeconds(1), 3, 0.95f, 3), events[id]);
        Assert.Equal(T0.AddHours(-1), events[dangling].EndedAt);

        var snapshot = metrics.Snapshot(queue.Dropped);
        Assert.Equal(2, snapshot.EventsPublished);
        Assert.Equal(0, snapshot.PendingEvents);
        Assert.Null(snapshot.LastError);
    }

    private static AnalyzedFrame Frame(double seconds) => new(
        "writer-cam",
        T0.AddSeconds(seconds),
        20,
        Geometry,
        [new Detection("person", ObjectCategory.Person, 0.9f, new BoundingBox(0.1f, 0.2f, 0.3f, 0.4f))]);

    private static DetectionEventState State(Guid id, double at, int count, int max, float peak, int frames) =>
        new(id, "writer-cam", ObjectCategory.Person, T0, T0.AddSeconds(at), null, count, max, peak, peak, frames);
}
