using Gozcu.Contracts.Events;
using Gozcu.Infrastructure.Messaging;
using Gozcu.Infrastructure.Persistence;
using Gozcu.RecordingService.Recording;

namespace Gozcu.RecordingService.Indexing;

/// <summary>
/// Biten parçaları veritabanına yazar ve <see cref="RecordingSegmentCompleted"/> olayını yayınlar.
/// Veritabanı geçici olarak erişilemezse parça kaybolmaz; yazma başarılı olana kadar yeniden denenir.
/// </summary>
public sealed class SegmentIndexer : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly SegmentQueue _queue;
    private readonly DatabaseReadiness _readiness;
    private readonly SegmentRepository _repository;
    private readonly IEventPublisher _publisher;
    private readonly RecordingMetrics _metrics;
    private readonly TimeProvider _time;
    private readonly ILogger<SegmentIndexer> _logger;

    public SegmentIndexer(
        SegmentQueue queue,
        DatabaseReadiness readiness,
        SegmentRepository repository,
        IEventPublisher publisher,
        RecordingMetrics metrics,
        TimeProvider time,
        ILogger<SegmentIndexer> logger)
    {
        _queue = queue;
        _readiness = readiness;
        _repository = repository;
        _publisher = publisher;
        _metrics = metrics;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _readiness.WaitAsync(stoppingToken);
            await foreach (var segment in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                var id = await SaveWithRetryAsync(segment, stoppingToken);
                _metrics.RecordIndexLatency(_time.GetUtcNow() - segment.EndedAt);
                _logger.LogInformation(
                    "Parça kaydedildi: {CameraId} {File} ({Size:N0} bayt, {Duration:0.0} sn)",
                    segment.CameraId, Path.GetFileName(segment.FilePath), segment.SizeBytes, (segment.EndedAt - segment.StartedAt).TotalSeconds);
                await PublishAsync(id, segment, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<long> SaveWithRetryAsync(CompletedSegment segment, CancellationToken stoppingToken)
    {
        while (true)
        {
            try
            {
                return await _repository.UpsertAsync(
                    segment.CameraId, segment.FilePath, segment.StartedAt, segment.EndedAt, segment.SizeBytes, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Parça veritabanına yazılamadı ({Message}); tekrar denenecek", ex.Message);
                await Task.Delay(RetryDelay, _time, stoppingToken);
            }
        }
    }

    private async Task PublishAsync(long id, CompletedSegment segment, CancellationToken stoppingToken)
    {
        try
        {
            await _publisher.PublishAsync(
                new RecordingSegmentCompleted(id, segment.CameraId, segment.FilePath, segment.StartedAt, segment.EndedAt, segment.SizeBytes),
                stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Parça olayı yayınlanamadı: {Message}", ex.Message);
        }
    }
}
