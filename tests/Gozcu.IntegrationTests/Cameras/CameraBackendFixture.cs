using Gozcu.Contracts.Events;
using Gozcu.Infrastructure.Messaging;
using Gozcu.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Gozcu.IntegrationTests.Cameras;

/// <summary>Kamera yönetimi testleri için PostgreSQL (şema uygulanmış) ve RabbitMQ konteynerleri.</summary>
public sealed class CameraBackendFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:3.13-management-alpine").Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public string PostgresConnectionString => _postgres.GetConnectionString();

    public Uri RabbitUri => new(_rabbit.GetConnectionString());

    public IOptions<RabbitMqOptions> RabbitOptions => Options.Create(new RabbitMqOptions { ConnectionString = RabbitUri });

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());
        DataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        await new DatabaseMigrator(DataSource, NullLogger<DatabaseMigrator>.Instance).MigrateAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
    }

    /// <summary>Kamera kataloğu olaylarını toplayan geçici kuyruk.</summary>
    public async Task<CatalogEventProbe> SubscribeAsync()
    {
        var factory = new ConnectionFactory { Uri = RabbitUri };
        var connection = await factory.CreateConnectionAsync();
        var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(EventRoutingKeys.Exchange, ExchangeType.Topic, durable: true);
        var queue = await channel.QueueDeclareAsync();
        await channel.QueueBindAsync(queue.QueueName, EventRoutingKeys.Exchange, EventRoutingKeys.CameraCatalogChanged);
        return new CatalogEventProbe(connection, channel, queue.QueueName);
    }
}

public sealed class CatalogEventProbe(IConnection connection, IChannel channel, string queue) : IAsyncDisposable
{
    public List<CameraCatalogChanged> Received { get; } = [];

    /// <summary>Koşulu sağlayan bir olay gelene kadar kuyruğu boşaltır.</summary>
    public async Task<CameraCatalogChanged> WaitForAsync(Func<CameraCatalogChanged, bool> match, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var found = Received.FirstOrDefault(match);
            if (found is not null)
            {
                return found;
            }

            var message = await channel.BasicGetAsync(queue, autoAck: true);
            if (message is null)
            {
                await Task.Delay(100);
            }
            else
            {
                Received.Add(EventSerializer.Deserialize<CameraCatalogChanged>(message.Body.Span)!);
            }
        }

        Assert.Fail($"{timeout.TotalSeconds} sn içinde beklenen kamera kataloğu olayı gelmedi");
        return null!;
    }

    public async ValueTask DisposeAsync()
    {
        await channel.DisposeAsync();
        await connection.DisposeAsync();
    }
}
