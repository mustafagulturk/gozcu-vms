using Gozcu.Contracts.Analysis;

namespace Gozcu.Contracts.Events;

/// <summary>
/// Analiz servisi bir kamerada bir nesne kategorisini (ör. insan) art arda yeterli karede gördüğünde yayınlar.
/// Aynı <paramref name="DetectionEventId"/> ile daha sonra <see cref="DetectionEventEnded"/> gelir.
/// </summary>
public sealed record DetectionEventStarted(
    Guid DetectionEventId,
    string CameraId,
    ObjectCategory Category,
    DateTimeOffset StartedAt,
    int ObjectCount,
    float Confidence) : IntegrationEvent
{
    public override string RoutingKey => EventRoutingKeys.DetectionEventStarted;
}
