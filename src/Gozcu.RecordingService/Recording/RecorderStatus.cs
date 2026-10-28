namespace Gozcu.RecordingService.Recording;

public enum RecorderState
{
    Starting,
    Recording,
    Retrying,
    Stopped,
}

public sealed record RecorderStatus(
    string CameraId,
    RecorderState State,
    DateTimeOffset StateSince,
    int SegmentsWritten,
    DateTimeOffset? LastSegmentAt,
    int RestartCount,
    string? LastError,
    DateTimeOffset? NextRetryAt);
