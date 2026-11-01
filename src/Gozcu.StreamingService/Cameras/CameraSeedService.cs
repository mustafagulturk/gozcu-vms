using Gozcu.Contracts.Cameras;
using Gozcu.Contracts.Events;
using Gozcu.Infrastructure.Cameras;
using Gozcu.Infrastructure.Messaging;
using Gozcu.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Gozcu.StreamingService.Cameras;

/// <summary>
/// İlk açılışta kamera tablosu boşsa yapılandırmadaki <c>Cameras</c> listesini yazar. Tablo doluysa yapılandırma
/// yok sayılır: kameraların tek doğruluk kaynağı veritabanıdır ve yönetim API'siyle değiştirilir.
/// </summary>
public sealed class CameraSeedService : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private readonly DatabaseReadiness _readiness;
    private readonly CameraRepository _repository;
    private readonly IOptions<CameraOptions> _cameras;
    private readonly ICameraCatalog _catalog;
    private readonly IEventPublisher _publisher;
    private readonly ILogger<CameraSeedService> _logger;

    public CameraSeedService(
        DatabaseReadiness readiness,
        CameraRepository repository,
        IOptions<CameraOptions> cameras,
        ICameraCatalog catalog,
        IEventPublisher publisher,
        ILogger<CameraSeedService> logger)
    {
        _readiness = readiness;
        _repository = repository;
        _cameras = cameras;
        _catalog = catalog;
        _publisher = publisher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var items = _cameras.Value.Items;
        if (items.Count == 0)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _readiness.WaitAsync(stoppingToken);
                var seeded = await _repository.SeedIfEmptyAsync(items, stoppingToken);
                if (seeded == 0)
                {
                    _logger.LogInformation(
                        "Kamera tablosu dolu; yapılandırmadaki {Count} kamera yok sayıldı (kameralar /api/cameras ile yönetilir)", items.Count);
                    return;
                }

                _logger.LogInformation("Kamera tablosu boştu; yapılandırmadaki {Count} kamera yazıldı", seeded);
                await CameraChangeNotifier.NotifyAsync(
                    _catalog, _publisher, new CameraCatalogChanged(null, CameraChangeKind.Seeded, null), _logger, stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Kameralar tohumlanamadı ({Message}); {Delay} sonra tekrar denenecek", ex.Message, RetryDelay);
                try
                {
                    await Task.Delay(RetryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}

/// <summary>Katalog değişikliğinden sonra yerel kataloğu yeniler ve diğer servislere olay yayınlar.</summary>
internal static class CameraChangeNotifier
{
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(5);

    public static async Task NotifyAsync(
        ICameraCatalog catalog, IEventPublisher publisher, CameraCatalogChanged change, ILogger logger, CancellationToken cancellationToken)
    {
        // Olay ve yerel yenileme hataları yazmayı geri almaz: diğer servisler periyodik yenilemeyle farkı kapatır.
        try
        {
            await catalog.RefreshAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Yerel kamera kataloğu yenilenemedi: {Message}", ex.Message);
        }

        // Aracı erişilemezse bağlantı denemesi uzun sürebilir; API yanıtını bekletmemek için süre sınırlıdır.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PublishTimeout);
        try
        {
            await publisher.PublishAsync(change, timeout.Token);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Kamera kataloğu olayı yayınlanamadı ({Message}); diğer servisler periyodik yenilemeyle güncellenecek", ex.Message);
        }
    }
}
