using Gozcu.Contracts.Analysis;
using Gozcu.Contracts.Events;
using Gozcu.Infrastructure.Messaging;
using Gozcu.Infrastructure.Persistence;
using Gozcu.Vision;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Gozcu.IntegrationTests.Analysis;

/// <summary>Analiz testleri için PostgreSQL (şema uygulanmış) ve RabbitMQ konteynerleri.</summary>
public sealed class AnalysisBackendFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:3.13-management-alpine").Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public DatabaseReadiness Readiness { get; } = new();

    public IOptions<RabbitMqOptions> RabbitOptions { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());
        DataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        await new DatabaseMigrator(DataSource, NullLogger<DatabaseMigrator>.Instance).MigrateAsync(CancellationToken.None);
        Readiness.MarkReady();
        RabbitOptions = Options.Create(new RabbitMqOptions { ConnectionString = new Uri(_rabbit.GetConnectionString()) });
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
    }

    /// <summary>"analysis.#" olaylarını toplayan geçici kuyruk.</summary>
    public async Task<EventSubscriber> SubscribeAsync()
    {
        var factory = new ConnectionFactory { Uri = RabbitOptions.Value.ConnectionString };
        var connection = await factory.CreateConnectionAsync();
        var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(EventRoutingKeys.Exchange, ExchangeType.Topic, durable: true);
        var queue = await channel.QueueDeclareAsync();
        await channel.QueueBindAsync(queue.QueueName, EventRoutingKeys.Exchange, "analysis.#");
        return new EventSubscriber(connection, channel, queue.QueueName);
    }
}

public sealed class EventSubscriber(IConnection connection, IChannel channel, string queue) : IAsyncDisposable
{
    public List<BasicGetResult> Received { get; } = [];

    /// <summary>En az <paramref name="count"/> mesaj gelene kadar kuyruğu boşaltır.</summary>
    public async Task<IReadOnlyList<BasicGetResult>> WaitForAsync(int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Received.Count < count && DateTime.UtcNow < deadline)
        {
            var message = await channel.BasicGetAsync(queue, autoAck: true);
            if (message is null)
            {
                await Task.Delay(100);
            }
            else
            {
                Received.Add(message);
            }
        }

        Assert.True(Received.Count >= count, $"{count} mesaj bekleniyordu, {Received.Count} geldi");
        return Received;
    }

    public async ValueTask DisposeAsync()
    {
        await channel.DisposeAsync();
        await connection.DisposeAsync();
    }
}

/// <summary>Her karede sabit bir insan tespiti döndüren sahte tespit edici; aldığı kareleri ve geometriyi kaydeder.</summary>
internal sealed class FixedDetector(int size) : IObjectDetector
{
    private readonly List<(int Length, LetterboxGeometry Geometry, byte PaddingSample)> _calls = [];

    public DetectorModelInfo Model { get; } = new("sabit", size, size);

    public IReadOnlyList<(int Length, LetterboxGeometry Geometry, byte PaddingSample)> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public IReadOnlyList<Detection> Detect(ReadOnlySpan<byte> bgrFrame, LetterboxGeometry geometry)
    {
        // Sol alt köşe: 16:9 kaynakta letterbox dolgusu (gri, 114) olmalı.
        var paddingSample = bgrFrame[((size - 1) * size * 3) + 1];
        lock (_calls)
        {
            _calls.Add((bgrFrame.Length, geometry, paddingSample));
        }

        return [new Detection("person", ObjectCategory.Person, 0.9f, new BoundingBox(0.25f, 0.25f, 0.5f, 0.5f))];
    }
}
