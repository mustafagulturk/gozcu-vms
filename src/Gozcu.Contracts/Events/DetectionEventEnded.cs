using Gozcu.Contracts.Analysis;

namespace Gozcu.Contracts.Events;

/// <summary>Kategori belirli bir süre boyunca hiç görülmediğinde (ya da analiz durduğunda) olay kapanır.</summary>
public sealed record DetectionEventEnded(
    Guid DetectionEventId,
    string CameraId,
    ObjectCategory Category,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int MaxObjectCount,
    float PeakConfidence,
    int FrameCount) : IntegrationEvent
{
    public override string RoutingKey => EventRoutingKeys.DetectionEventEnded;
}
