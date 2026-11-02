using Gozcu.AnalysisService;
using Gozcu.AnalysisService.Events;
using Gozcu.Contracts.Analysis;
using Gozcu.Vision;

namespace Gozcu.UnitTests.Analysis;

public class DetectionEventTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly DetectionEventOptions Options = new() { MinConfidence = 0.5f, MinConsecutiveFrames = 2, EndAfter = TimeSpan.FromSeconds(5) };

    [Fact]
    public void Single_frame_detection_does_not_open_an_event()
    {
        var tracker = NewTracker();

        Assert.Empty(tracker.Process(At(0), [Person(0.9f)]));
        Assert.Empty(tracker.Process(At(0.5), []));
        Assert.Empty(tracker.Process(At(1.0), [Person(0.9f)]));
        Assert.Empty(tracker.Process(At(1.5), []));
        Assert.Empty(tracker.OpenEvents);
    }

    [Fact]
    public void Event_opens_after_consecutive_frames_and_starts_at_first_sighting()
    {
        var tracker = NewTracker();

        Assert.Empty(tracker.Process(At(0), [Person(0.6f)]));
        var started = Assert.Single(tracker.Process(At(0.5), [Person(0.8f), Person(0.7f)]));

        Assert.Equal(DetectionEventChange.Started, started.Change);
        Assert.Equal(ObjectCategory.Person, started.Event.Category);
        Assert.Equal("cam1", started.Event.CameraId);
        Assert.Equal(At(0), started.Event.StartedAt);
        Assert.Equal(At(0.5), started.Event.LastSeenAt);
        Assert.Null(started.Event.EndedAt);
        Assert.Equal(2, started.Event.ObjectCount);
        Assert.Equal(2, started.Event.MaxCount);
        Assert.Equal(0.8f, started.Event.Confidence);
        Assert.Equal(0.8f, started.Event.PeakConfidence);
        Assert.Equal(2, started.Event.FrameCount);
        Assert.NotEqual(Guid.Empty, started.Event.Id);
        Assert.Single(tracker.OpenEvents);
    }

    [Fact]
    public void Detections_below_event_confidence_are_ignored()
    {
        var tracker = NewTracker();

        Assert.Empty(tracker.Process(At(0), [Person(0.49f)]));
        Assert.Empty(tracker.Process(At(0.5), [Person(0.4f)]));
        Assert.Empty(tracker.Process(At(1.0), [Person(0.3f)]));
        Assert.Empty(tracker.OpenEvents);
    }

    [Fact]
    public void Updates_track_max_count_and_peak_confidence_with_same_id()
    {
        var tracker = NewTracker();
        tracker.Process(At(0), [Person(0.6f)]);
        var id = tracker.Process(At(0.5), [Person(0.6f)])[0].Event.Id;

        var update1 = Assert.Single(tracker.Process(At(1.0), [Person(0.95f), Person(0.7f), Person(0.55f)]));
        var update2 = Assert.Single(tracker.Process(At(1.5), [Person(0.65f)]));

        Assert.Equal(DetectionEventChange.Updated, update1.Change);
        Assert.Equal(id, update1.Event.Id);
        Assert.Equal(3, update1.Event.ObjectCount);
        Assert.Equal(id, update2.Event.Id);
        Assert.Equal(1, update2.Event.ObjectCount);
        Assert.Equal(3, update2.Event.MaxCount);
        Assert.Equal(0.65f, update2.Event.Confidence);
        Assert.Equal(0.95f, update2.Event.PeakConfidence);
        Assert.Equal(4, update2.Event.FrameCount);
    }

    [Fact]
    public void Short_gaps_keep_the_event_open()
    {
        var tracker = NewTracker();
        tracker.Process(At(0), [Person(0.9f)]);
        tracker.Process(At(0.5), [Person(0.9f)]);

        // 4,5 sn boyunca görünmedi (< 5 sn), sonra yeniden göründü: aynı olay sürer.
        for (var t = 1.0; t <= 4.5; t += 0.5)
        {
            Assert.Empty(tracker.Process(At(t), []));
        }

        var update = Assert.Single(tracker.Process(At(5.0), [Person(0.9f)]));
        Assert.Equal(DetectionEventChange.Updated, update.Change);
    }

    [Fact]
    public void Event_ends_after_gap_with_end_time_at_last_sighting()
    {
        var tracker = NewTracker();
        tracker.Process(At(0), [Person(0.9f)]);
        tracker.Process(At(0.5), [Person(0.9f)]);
        tracker.Process(At(1.0), [Person(0.9f), Person(0.8f)]);

        Assert.Empty(tracker.Process(At(5.5), []));
        var ended = Assert.Single(tracker.Process(At(6.0), []));

        Assert.Equal(DetectionEventChange.Ended, ended.Change);
        Assert.Equal(At(1.0), ended.Event.EndedAt);
        Assert.Equal(At(0), ended.Event.StartedAt);
        Assert.Equal(2, ended.Event.MaxCount);
        Assert.Equal(3, ended.Event.FrameCount);
        Assert.Empty(tracker.OpenEvents);
        Assert.Empty(tracker.Process(At(6.5), []));
    }

    [Fact]
    public void Reappearing_after_close_opens_a_new_event_with_new_id()
    {
        var ids = new Queue<Guid>([Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("00000000-0000-0000-0000-000000000002")]);
        var tracker = new DetectionEventTracker("cam1", Options, ids.Dequeue);
        tracker.Process(At(0), [Person(0.9f)]);
        var first = tracker.Process(At(0.5), [Person(0.9f)])[0].Event.Id;
        tracker.Process(At(6.0), []);

        // Yeniden görülme yine debounce'tan geçer.
        Assert.Empty(tracker.Process(At(10.0), [Person(0.9f)]));
        var started = Assert.Single(tracker.Process(At(10.5), [Person(0.9f)]));

        Assert.Equal(DetectionEventChange.Started, started.Change);
        Assert.NotEqual(first, started.Event.Id);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000002"), started.Event.Id);
        Assert.Equal(At(10.0), started.Event.StartedAt);
        Assert.Equal(2, started.Event.FrameCount);
    }

    [Fact]
    public void Qualifying_frame_after_long_stall_closes_old_event_and_starts_debounce_again()
    {
        var tracker = NewTracker();
        tracker.Process(At(0), [Person(0.9f)]);
        tracker.Process(At(0.5), [Person(0.9f)]);

        // Akış 10 sn takıldı; gelen ilk kare nitelikli olsa da eski olay kapanır.
        var transitions = tracker.Process(At(10.5), [Person(0.9f)]);

        var ended = Assert.Single(transitions);
        Assert.Equal(DetectionEventChange.Ended, ended.Change);
        Assert.Equal(At(0.5), ended.Event.EndedAt);
        Assert.Equal(DetectionEventChange.Started, Assert.Single(tracker.Process(At(11.0), [Person(0.9f)])).Change);
    }

    [Fact]
    public void Categories_are_tracked_independently()
    {
        var tracker = NewTracker();

        Assert.Empty(tracker.Process(At(0), [Person(0.9f)]));
        var t1 = tracker.Process(At(0.5), [Person(0.9f), Vehicle(0.8f)]);
        Assert.Equal([(DetectionEventChange.Started, ObjectCategory.Person)], t1.Select(t => (t.Change, t.Event.Category)));

        var t2 = tracker.Process(At(1.0), [Vehicle(0.85f), Vehicle(0.7f)]);
        var vehicle = Assert.Single(t2);
        Assert.Equal((DetectionEventChange.Started, ObjectCategory.Vehicle), (vehicle.Change, vehicle.Event.Category));
        Assert.Equal(2, vehicle.Event.MaxCount);

        // İnsan 5 sn görülmedi, araç görülmeye devam ediyor.
        var t3 = tracker.Process(At(5.5), [Vehicle(0.9f)]);
        Assert.Equal(
            [(DetectionEventChange.Ended, ObjectCategory.Person), (DetectionEventChange.Updated, ObjectCategory.Vehicle)],
            t3.Select(t => (t.Change, t.Event.Category)));
        Assert.Equal(ObjectCategory.Vehicle, Assert.Single(tracker.OpenEvents).Category);
    }

    [Fact]
    public void Close_all_ends_open_events_at_last_sighting_and_drops_pending_candidates()
    {
        var tracker = NewTracker();
        tracker.Process(At(0), [Person(0.9f)]);
        tracker.Process(At(0.5), [Person(0.9f), Vehicle(0.9f)]);

        var closed = tracker.CloseAll();

        var ended = Assert.Single(closed);
        Assert.Equal(DetectionEventChange.Ended, ended.Change);
        Assert.Equal(ObjectCategory.Person, ended.Event.Category);
        Assert.Equal(At(0.5), ended.Event.EndedAt);
        Assert.Empty(tracker.OpenEvents);
        Assert.Empty(tracker.CloseAll());

        // Araç adayı atıldı: bir sonraki kare debounce'u baştan başlatır.
        Assert.Empty(tracker.Process(At(1.0), [Vehicle(0.9f)]));
    }

    [Fact]
    public void Single_frame_threshold_opens_immediately()
    {
        var tracker = new DetectionEventTracker("cam1", new DetectionEventOptions { MinConsecutiveFrames = 1, MinConfidence = 0.5f, EndAfter = TimeSpan.FromSeconds(5) });

        var started = Assert.Single(tracker.Process(At(0), [Vehicle(0.7f)]));
        Assert.Equal(DetectionEventChange.Started, started.Change);
        Assert.Equal(1, started.Event.FrameCount);
    }

    private static DetectionEventTracker NewTracker() => new("cam1", Options);

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    private static Detection Person(float confidence) => new("person", ObjectCategory.Person, confidence, new BoundingBox(0.1f, 0.1f, 0.2f, 0.4f));

    private static Detection Vehicle(float confidence) => new("car", ObjectCategory.Vehicle, confidence, new BoundingBox(0.5f, 0.5f, 0.3f, 0.2f));
}
