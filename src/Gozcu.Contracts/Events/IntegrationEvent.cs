namespace Gozcu.Contracts.Events;

/// <summary>RabbitMQ üzerinden yayınlanan tüm olayların ortak tabanı.</summary>
public abstract record IntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Topic exchange üzerindeki yönlendirme anahtarı.</summary>
    public abstract string RoutingKey { get; }
}
