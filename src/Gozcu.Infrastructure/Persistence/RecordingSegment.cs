namespace Gozcu.Infrastructure.Persistence;

public sealed record RecordingSegment(
    long Id,
    string CameraId,
    string FilePath,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    long SizeBytes);
