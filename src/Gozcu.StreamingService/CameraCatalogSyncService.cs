using Gozcu.Contracts.Cameras;
using Gozcu.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Gozcu.StreamingService;

/// <summary>Yapılandırmadaki kameraları veritabanına yazar (1. aşama; ileride API servisi yönetecek).</summary>
public sealed class CameraCatalogSyncService : BackgroundService
{
    private readonly DatabaseReadiness _readiness;
    private readonly CameraRepository _repository;
    private readonly IOptions<CameraOptions> _cameras;
    private readonly ILogger<CameraCatalogSyncService> _logger;

    public CameraCatalogSyncService(
        DatabaseReadiness readiness,
        CameraRepository repository,
        IOptions<CameraOptions> cameras,
        ILogger<CameraCatalogSyncService> logger)
    {
        _readiness = readiness;
        _repository = repository;
        _cameras = cameras;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _readiness.WaitAsync(stoppingToken);
            foreach (var camera in _cameras.Value.Items)
            {
                await _repository.UpsertAsync(camera, stoppingToken);
            }

            _logger.LogInformation("{Count} kamera veritabanına senkronize edildi", _cameras.Value.Items.Count);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kameralar veritabanına yazılamadı");
        }
    }
}
