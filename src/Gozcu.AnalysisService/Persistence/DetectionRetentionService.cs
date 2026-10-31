using Gozcu.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Gozcu.AnalysisService.Persistence;

/// <summary>Saklama süresi (Analysis:FrameRetention) dolan tespit karelerini saatte bir siler. Olaylar saklanır.</summary>
public sealed class DetectionRetentionService : BackgroundService
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    private readonly AnalysisOptions _options;
    private readonly DetectionRepository _repository;
    private readonly DatabaseReadiness _readiness;
    private readonly TimeProvider _time;
    private readonly ILogger<DetectionRetentionService> _logger;

    public DetectionRetentionService(
        IOptions<AnalysisOptions> options,
        DetectionRepository repository,
        DatabaseReadiness readiness,
        TimeProvider time,
        ILogger<DetectionRetentionService> logger)
    {
        _options = options.Value;
        _repository = repository;
        _readiness = readiness;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _readiness.WaitAsync(stoppingToken);
            using var timer = new PeriodicTimer(CheckInterval, _time);
            do
            {
                try
                {
                    var cutoff = _time.GetUtcNow() - _options.FrameRetention;
                    var deleted = await _repository.DeleteFramesOlderThanAsync(cutoff, stoppingToken);
                    if (deleted > 0)
                    {
                        _logger.LogInformation("Saklama süresi dolan {Count} tespit karesi silindi ({Cutoff:u} öncesi)", deleted, cutoff);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Eski tespit kareleri silinemedi: {Message}", ex.Message);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }
}
