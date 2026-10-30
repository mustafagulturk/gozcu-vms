using Gozcu.Contracts.Cameras;

namespace Gozcu.Contracts.Events;

/// <summary>Akış servisi bir kameranın sağlık durumu değiştiğinde yayınlar.</summary>
public sealed record CameraStatusChanged(
    string CameraId,
    CameraHealthState PreviousState,
    CameraHealthState CurrentState,
    string? Reason) : IntegrationEvent
{
    public override string RoutingKey => EventRoutingKeys.CameraStatusChanged;
}
