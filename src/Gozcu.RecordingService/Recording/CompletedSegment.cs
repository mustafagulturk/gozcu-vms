namespace Gozcu.RecordingService.Recording;

/// <summary>ffmpeg'in yazmayı bitirdiği bir kayıt parçası.</summary>
public sealed record CompletedSegment(
    string CameraId,
    string FilePath,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    long SizeBytes,
    DateTimeOffset DetectedAt);
