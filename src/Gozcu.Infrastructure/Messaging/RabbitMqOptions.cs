namespace Gozcu.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public Uri ConnectionString { get; set; } = new("amqp://guest:guest@localhost:5672/");

    public string Exchange { get; set; } = Gozcu.Contracts.Events.EventRoutingKeys.Exchange;

    /// <summary>RabbitMQ yönetim arayüzünde görünen istemci adı.</summary>
    public string ClientName { get; set; } = "gozcu";
}
