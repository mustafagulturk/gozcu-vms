using Gozcu.Contracts.Events;

namespace Gozcu.Infrastructure.Messaging;

public interface IEventPublisher
{
    /// <summary>Olayı topic exchange'e yayınlar. Aracı erişilemezse istisna fırlatır.</summary>
    Task PublishAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken = default);
}
