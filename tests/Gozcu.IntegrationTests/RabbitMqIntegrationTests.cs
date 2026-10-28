using Gozcu.Contracts.Cameras;
using Gozcu.Contracts.Events;
using Gozcu.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace Gozcu.IntegrationTests;

public sealed class RabbitMqIntegrationTests : IAsyncLifetime
{
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:3.13-management-alpine").Build();

    public Task InitializeAsync() => _rabbit.StartAsync();

    public async Task DisposeAsync() => await _rabbit.DisposeAsync();

    [Fact]
    public async Task Published_event_reaches_subscriber_bound_by_routing_key()
    {
        var options = Options.Create(new RabbitMqOptions { ConnectionString = new Uri(_rabbit.GetConnectionString()) });
        await using var connection = new RabbitMqConnection(options, NullLogger<RabbitMqConnection>.Instance);
        await using var publisher = new RabbitMqEventPublisher(connection, options);

        // Abone: kamera olaylarını dinleyen geçici kuyruk (ör. ileride API/olay servisi).
        var factory = new ConnectionFactory { Uri = new Uri(_rabbit.GetConnectionString()) };
        await using var subscriberConnection = await factory.CreateConnectionAsync();
        await using var channel = await subscriberConnection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(EventRoutingKeys.Exchange, ExchangeType.Topic, durable: true);
        var queue = await channel.QueueDeclareAsync();
        await channel.QueueBindAsync(queue.QueueName, EventRoutingKeys.Exchange, "camera.#");

        var sent = new CameraStatusChanged("cam1", CameraHealthState.Online, CameraHealthState.Offline, "Connection refused");
        await publisher.PublishAsync(sent);
        await publisher.PublishAsync(new RecordingSegmentCompleted(1, "cam1", "/r/a.mp4", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1));

        BasicGetResult? message = null;
        for (var i = 0; i < 50 && message is null; i++)
        {
            message = await channel.BasicGetAsync(queue.QueueName, autoAck: true);
            if (message is null)
            {
                await Task.Delay(100);
            }
        }

        Assert.NotNull(message);
        Assert.Equal(EventRoutingKeys.CameraStatusChanged, message.RoutingKey);
        Assert.Equal(nameof(CameraStatusChanged), message.BasicProperties.Type);
        Assert.Equal(sent, EventSerializer.Deserialize<CameraStatusChanged>(message.Body.Span));

        // Kayıt olayı "camera.#" bağlamasına düşmemeli.
        Assert.Null(await channel.BasicGetAsync(queue.QueueName, autoAck: true));
    }
}
