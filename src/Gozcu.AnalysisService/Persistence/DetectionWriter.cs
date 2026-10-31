using Gozcu.AnalysisService.Events;
using Gozcu.AnalysisService.Pipeline;
using Gozcu.Contracts.Events;
using Gozcu.Infrastructure.Messaging;
using Gozcu.Infrastructure.Persistence;
using Gozcu.Media.Processes;

namespace Gozcu.AnalysisService.Persistence;

/// <summary>
/// <see cref="DetectionQueue"/>'nun tek tüketicisi. Tespit karelerini toplu yazar (en fazla 200 satır ya da 1 sn),
/// olay geçişlerini veritabanına yazar ve RabbitMQ'ya yayınlar (başladı → <see cref="DetectionEventStarted"/>,
/// bitti → <see cref="DetectionEventEnded"/>). Ara güncellemeler yalnızca veritabanındaki satırı günceller ve olay başına
/// en fazla 2 sn'de bir yazılır.
/// </summary>
/// <remarks>
/// Veritabanı yazılamazsa artan aralıklarla yeniden denenir; bu sırada kuyruk boşaltılmaya devam eder, böylece analiz
/// asla beklemez. Bellekteki kare tamponu dolarsa yeni kareler atılır ve sayılır; olay durumları olay başına tek
/// kayıtta birleştirildiğinden sınırlıdır.
/// Bilinen kısıt (2. aşamadaki gibi): outbox yok. Olay önce veritabanına yazılır, sonra yayınlanır; yayın başarısız
/// olursa (ya da servis ikisi arasında çökerse) mesaj kaybolur, yeniden denenmez. Veritabanı her zaman doğru kaynaktır.
/// </remarks>
public sealed class DetectionWriter : BackgroundService
{
    internal const int MaxBatchSize = 200;
    internal const int MaxBufferedFrames = 5000;
    internal static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(5);
    private static readonly ReconnectPolicy RetryPolicy = new() { InitialDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(30) };

    private readonly DetectionQueue _queue;
    private readonly DatabaseReadiness _readiness;
    private readonly DetectionRepository _repository;
    private readonly IEventPublisher _publisher;
    private readonly DetectionWriterMetrics _metrics;
    private readonly TimeProvider _time;
    private readonly ILogger<DetectionWriter> _logger;

    private readonly List<DetectionFrameRecord> _frames = [];
    private readonly Dictionary<Guid, PendingEvent> _events = [];
    private DateTimeOffset? _framesSince;
    private bool _recovered;
    private int _failures;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;

    public DetectionWriter(
        DetectionQueue queue,
        DatabaseReadiness readiness,
        DetectionRepository repository,
        IEventPublisher publisher,
        DetectionWriterMetrics metrics,
        TimeProvider time,
        ILogger<DetectionWriter> logger)
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
            while (true)
            {
                Drain();
                await FlushAsync(force: false, stoppingToken);
                await WaitForWorkAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        // Kapanış: analizciler önce durur ve açık olayları kapatır; kalanları kısa bir süre içinde yazmayı dene.
        using var shutdown = new CancellationTokenSource(ShutdownFlushTimeout);
        try
        {
            Drain();
            await FlushAsync(force: true, shutdown.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Kapanırken tespitler yazılamadı: {Message}", ex.Message);
        }
    }

    /// <summary>Kuyruktaki tüm işleri belleğe alır (yalnızca bellek işlemi; hızlıdır).</summary>
    private void Drain()
    {
        var now = _time.GetUtcNow();
        while (_queue.Reader.TryRead(out var output))
        {
            if (output.Frame is { Detections.Count: > 0 } frame)
            {
                if (_frames.Count >= MaxBufferedFrames)
                {
                    _metrics.FrameDropped();
                }
                else
                {
                    _frames.Add(ToRecord(frame));
                    _framesSince ??= now;
                }
            }

            foreach (var transition in output.Transitions)
            {
                Apply(transition);
            }
        }

        _metrics.SetBacklog(_frames.Count, _events.Count);
    }

    private void Apply(DetectionEventTransition transition)
    {
        var state = transition.Event;
        if (!_events.TryGetValue(state.Id, out var pending))
        {
            pending = new PendingEvent(state);
            _events[state.Id] = pending;
        }

        pending.Latest = state;
        pending.Dirty = true;
        switch (transition.Change)
        {
            case DetectionEventChange.Started:
                pending.StartedState = state;
                break;
            case DetectionEventChange.Ended:
                pending.Ended = true;
                break;
        }
    }

    private async Task FlushAsync(bool force, CancellationToken cancellationToken)
    {
        if (!_readiness.IsReady)
        {
            return;
        }

        var now = _time.GetUtcNow();
        if (!force && now < _retryAt)
        {
            return;
        }

        try
        {
            if (!_recovered)
            {
                // Önceki çalıştırma çöktüyse açık kalmış olaylar var; yenileri yazılmadan önce kapatılır.
                var closed = await _repository.CloseDanglingEventsAsync(cancellationToken);
                _recovered = true;
                if (closed > 0)
                {
                    _logger.LogWarning("Önceki çalıştırmadan açık kalan {Count} olay kapatıldı", closed);
                }
            }

            if (_frames.Count > 0 && (force || _frames.Count >= MaxBatchSize || now - _framesSince >= FlushInterval))
            {
                while (_frames.Count > 0)
                {
                    var batch = _frames.GetRange(0, Math.Min(MaxBatchSize, _frames.Count));
                    await _repository.InsertFramesAsync(batch, cancellationToken);
                    _frames.RemoveRange(0, batch.Count);
                    _metrics.FramesWritten(batch.Count, _time.GetUtcNow());
                }

                _framesSince = null;
            }

            var due = _events.Values.Where(e => e.IsDue(now, force)).ToList();
            if (due.Count > 0)
            {
                await _repository.UpsertEventsAsync(due.Select(e => ToRecord(e.Latest)).ToList(), cancellationToken);
                _metrics.EventsWritten(due.Count, _time.GetUtcNow());
                foreach (var pending in due)
                {
                    pending.Dirty = false;
                    pending.WrittenAt = now;
                }

                await PublishAsync(due, cancellationToken);
            }

            _failures = 0;
            _retryAt = DateTimeOffset.MinValue;
            _metrics.SetError(null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _failures++;
            var delay = RetryPolicy.GetDelay(_failures);
            _retryAt = now + delay;
            _metrics.SetError(ex.Message);
            _logger.LogWarning("Tespitler veritabanına yazılamadı ({Message}); {Delay} sonra tekrar denenecek", ex.Message, delay);
        }
        finally
        {
            _metrics.SetBacklog(_frames.Count, _events.Count);
        }
    }

    private async Task PublishAsync(List<PendingEvent> written, CancellationToken cancellationToken)
    {
        foreach (var pending in written)
        {
            if (pending.StartedState is { } started)
            {
                pending.StartedState = null;
                await TryPublishAsync(
                    new DetectionEventStarted(started.Id, started.CameraId, started.Category, started.StartedAt, started.ObjectCount, started.Confidence),
                    cancellationToken);
            }

            if (pending.Ended)
            {
                var ended = pending.Latest;
                _events.Remove(ended.Id);
                await TryPublishAsync(
                    new DetectionEventEnded(
                        ended.Id, ended.CameraId, ended.Category, ended.StartedAt, ended.EndedAt ?? ended.LastSeenAt,
                        ended.MaxCount, ended.PeakConfidence, ended.FrameCount),
                    cancellationToken);
            }
        }
    }

    private async Task TryPublishAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        // RabbitMQ erişilemezse bağlantı denemesi uzun sürebilir; yazıcıyı bekletmemek için süre sınırı var.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PublishTimeout);
        try
        {
            await _publisher.PublishAsync(integrationEvent, timeout.Token);
            _metrics.EventPublished();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _metrics.PublishFailed();
            _logger.LogWarning("Tespit olayı yayınlanamadı ({RoutingKey}): {Message}", integrationEvent.RoutingKey, ex.Message);
        }
    }

    private async Task WaitForWorkAsync(CancellationToken cancellationToken)
    {
        // Bekleyen iş varsa kısa aralıklarla (toplu yazma süresi, güncelleme kısıtı, yeniden deneme) uyan.
        // Açılış kurtarması da veritabanı hazır olur olmaz, yeni iş beklemeden yapılır.
        var pending = !_recovered || _frames.Count > 0 || _events.Values.Any(e => e.Dirty);
        if (!pending)
        {
            await _queue.Reader.WaitToReadAsync(cancellationToken);
            return;
        }

        using var timeout = new CancellationTokenSource(PollInterval, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await _queue.Reader.WaitToReadAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal static DetectionFrameRecord ToRecord(AnalyzedFrame frame) => new(
        frame.CameraId,
        frame.CapturedAt,
        (float)frame.InferenceMs,
        frame.Detections
            .Select(d => new StoredDetection(d.Label, d.Category, d.Confidence, d.Box.X, d.Box.Y, d.Box.Width, d.Box.Height))
            .ToList());

    internal static DetectionEventRecord ToRecord(DetectionEventState state) => new(
        state.Id,
        state.CameraId,
        state.Category,
        state.StartedAt,
        state.EndedAt,
        state.MaxCount,
        state.PeakConfidence,
        state.FrameCount);

    private sealed class PendingEvent(DetectionEventState latest)
    {
        public DetectionEventState Latest { get; set; } = latest;

        /// <summary>Yayınlanmayı bekleyen başlangıç durumu.</summary>
        public DetectionEventState? StartedState { get; set; }

        public bool Ended { get; set; }

        /// <summary>Veritabanına yazılmamış değişiklik var.</summary>
        public bool Dirty { get; set; }

        public DateTimeOffset? WrittenAt { get; set; }

        /// <summary>Başlangıç ve bitiş hemen yazılır; ara güncellemeler olay başına <see cref="UpdateInterval"/>'da bir.</summary>
        public bool IsDue(DateTimeOffset now, bool force) =>
            Dirty && (force || StartedState is not null || Ended || WrittenAt is null || now - WrittenAt >= UpdateInterval);
    }
}
