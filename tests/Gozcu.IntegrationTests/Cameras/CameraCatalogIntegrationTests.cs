using Gozcu.Contracts.Cameras;
using Gozcu.Contracts.Events;
using Gozcu.Infrastructure.Cameras;
using Gozcu.Infrastructure.Messaging;
using Gozcu.Infrastructure.Persistence;
using Gozcu.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gozcu.IntegrationTests.Cameras;

public sealed class CameraCatalogIntegrationTests(CameraBackendFixture backend) : IClassFixture<CameraBackendFixture>
{
    [Fact]
    public async Task Catalog_is_refreshed_by_rabbitmq_event_long_before_periodic_refresh()
    {
        var readiness = new DatabaseReadiness();
        readiness.MarkReady();
        var repository = new CameraRepository(backend.DataSource);
        using var catalog = new CameraCatalog(repository, readiness, NullLogger<CameraCatalog>.Instance);
        await using var connection = new RabbitMqConnection(backend.RabbitOptions, NullLogger<RabbitMqConnection>.Instance);
        var subscriber = new RabbitMqEventSubscriber(connection, backend.RabbitOptions, NullLogger<RabbitMqEventSubscriber>.Instance);
        using var refresher = new CameraCatalogRefresher(
            catalog,
            subscriber,
            Options.Create(new CameraCatalogOptions { RefreshInterval = TimeSpan.FromMinutes(10) }),
            NullLogger<CameraCatalogRefresher>.Instance);
        await refresher.StartAsync(CancellationToken.None);
        await catalog.WaitUntilLoadedAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // Başka bir servis (akış servisi) veritabanına yazar ve olay yayınlar.
        var id = "olay-" + Guid.NewGuid().ToString("N")[..8];
        await repository.InsertAsync(new CameraDefinition { Id = id, Name = "Olay", RtspUrl = new Uri("rtsp://h/x") }, CancellationToken.None);
        await using var otherConnection = new RabbitMqConnection(backend.RabbitOptions, NullLogger<RabbitMqConnection>.Instance);
        await using var publisher = new RabbitMqEventPublisher(otherConnection, backend.RabbitOptions);

        // Abonelik kurulmadan yayınlanan olay kaybolabilir (geçici kuyruk); bu yüzden görülene kadar yeniden yayınlanır.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (catalog.Find(id) is null)
        {
            Assert.True(DateTime.UtcNow < deadline, "katalog olayla yenilenmeliydi");
            await publisher.PublishAsync(new CameraCatalogChanged(id, CameraChangeKind.Created, 1));
            await Task.Delay(300);
        }

        // Silme de aynı yoldan yansır.
        var version = catalog.Version;
        await repository.DeleteAsync(id, null, CancellationToken.None);
        await publisher.PublishAsync(new CameraCatalogChanged(id, CameraChangeKind.Deleted, null));
        await Eventually.TrueAsync(() => catalog.Find(id) is null, TimeSpan.FromSeconds(10), "silinen kamera katalogdan çıkmalı");
        Assert.True(catalog.Version > version);

        await refresher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Periodic_refresh_closes_the_gap_when_event_is_lost()
    {
        var readiness = new DatabaseReadiness();
        readiness.MarkReady();
        var repository = new CameraRepository(backend.DataSource);
        using var catalog = new CameraCatalog(repository, readiness, NullLogger<CameraCatalog>.Instance);

        // Aracı erişilemez: abonelik hiç kurulamaz.
        var deadBroker = Options.Create(new RabbitMqOptions { ConnectionString = new Uri("amqp://guest:guest@127.0.0.1:1/") });
        await using var connection = new RabbitMqConnection(deadBroker, NullLogger<RabbitMqConnection>.Instance);
        var subscriber = new RabbitMqEventSubscriber(connection, deadBroker, NullLogger<RabbitMqEventSubscriber>.Instance);
        using var refresher = new CameraCatalogRefresher(
            catalog,
            subscriber,
            Options.Create(new CameraCatalogOptions { RefreshInterval = TimeSpan.FromMilliseconds(500) }),
            NullLogger<CameraCatalogRefresher>.Instance);
        await refresher.StartAsync(CancellationToken.None);
        await catalog.WaitUntilLoadedAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        var id = "periyot-" + Guid.NewGuid().ToString("N")[..8];
        await repository.InsertAsync(new CameraDefinition { Id = id, Name = "P", RtspUrl = new Uri("rtsp://h/p") }, CancellationToken.None);

        await Eventually.TrueAsync(() => catalog.Find(id) is not null, TimeSpan.FromSeconds(10), "periyodik yenileme farkı kapatmalı");
        await refresher.StopAsync(CancellationToken.None);
    }
}
