namespace Gozcu.Contracts.Events;

/// <summary>Kayıt servisi bir kayıt parçasını diske yazmayı bitirdiğinde yayınlar.</summary>
public sealed record RecordingSegmentCompleted(
    long SegmentId,
    string CameraId,
    string FilePath,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    long SizeBytes) : IntegrationEvent
{
    public override string RoutingKey => EventRoutingKeys.RecordingSegmentCompleted;
}
