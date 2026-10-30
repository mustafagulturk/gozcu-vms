namespace Gozcu.Contracts.Events;

public static class EventRoutingKeys
{
    /// <summary>Tüm servislerin olay yayınladığı topic exchange.</summary>
    public const string Exchange = "gozcu.events";

    public const string CameraStatusChanged = "camera.status.changed";

    public const string RecordingSegmentCompleted = "recording.segment.completed";
}
