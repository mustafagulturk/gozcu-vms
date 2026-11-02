using System.Threading.Channels;
using Gozcu.Contracts.Events;
using Gozcu.Infrastructure.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gozcu.Infrastructure.Cameras;

/// <summary>
/// Kamera kataloğunu güncel tutar: açılışta ilk yüklemeyi yapar (veritabanı hazır olana kadar yeniden dener),
/// <c>camera.catalog.changed</c> olayı geldiğinde ve <see cref="CameraCatalogOptions.RefreshInterval"/> aralıkla
/// veritabanından yeniden okur. Olay içeriğine güvenilmez; olay yalnızca "hemen yenile" sinyalidir.
/// </summary>
public sealed class CameraCatalogRefresher : BackgroundService
{
    private readonly ICameraCatalog _catalog;
    private readonly RabbitMqEventSubscriber _subscriber;
    private readonly CameraCatalogOptions _options;
    private readonly ILogger<CameraCatalogRefresher> _logger;

    // Bekleyen tek bir "yenile" isteği yeter; art arda gelen olaylar birleşir.
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public CameraCatalogRefresher(
        ICameraCatalog catalog,
        RabbitMqEventSubscriber subscriber,
        IOptions<CameraCatalogOptions> options,
        ILogger<CameraCatalogRefresher> logger)
    {
        _catalog = catalog;
        _subscriber = subscriber;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Bir sonraki döngüde kataloğun yeniden okunmasını ister.</summary>
    public void RequestRefresh() => _requests.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscription = _subscriber.RunAsync(
            EventRoutingKeys.CameraCatalogChanged,
            (_, body, _) =>
            {
                var changed = TryDeserialize(body);
                _logger.LogDebug("Kamera kataloğu olayı alındı: {CameraId} {Kind}", changed?.CameraId ?? "-", changed?.Kind);
                RequestRefresh();
                return Task.CompletedTask;
            },
            // Abonelik kurulmadan önce yapılmış değişiklikler olay olarak gelmez; abonelik her kurulduğunda yeniden oku.
            _ =>
            {
                RequestRefresh();
                return Task.CompletedTask;
            },
            stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan wait;
                try
                {
                    await _catalog.RefreshAsync(stoppingToken);
                    wait = _options.RefreshInterval;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    wait = _options.RetryDelay;
                    _logger.LogWarning("Kamera kataloğu okunamadı ({Message}); {Delay} sonra tekrar denenecek", ex.Message, wait);
                }

                await WaitForRequestAsync(wait, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await subscription;
    }

    private async Task WaitForRequestAsync(TimeSpan timeout, CancellationToken stoppingToken)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timer.CancelAfter(timeout);
        try
        {
            await _requests.Reader.ReadAsync(timer.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Süre doldu: periyodik yenileme.
        }
    }

    private static CameraCatalogChanged? TryDeserialize(ReadOnlyMemory<byte> body)
    {
        try
        {
            return EventSerializer.Deserialize<CameraCatalogChanged>(body.Span);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
