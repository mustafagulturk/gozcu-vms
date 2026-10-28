using Gozcu.Contracts.Cameras;
using Gozcu.Media.Ffmpeg;

namespace Gozcu.StreamingService.Health;

public sealed record CameraHealthSnapshot(
    string CameraId,
    string Name,
    CameraHealthState State,
    DateTimeOffset StateSince,
    DateTimeOffset? LastMediaAt,
    double? IngestRate,
    StreamInfo? Stream,
    int ReconnectCount,
    string? LastError,
    DateTimeOffset? NextRetryAt,
    Uri? HlsUrl);

public sealed record CameraStateTransition(string CameraId, CameraHealthState Previous, CameraHealthState Current, string? Reason);
