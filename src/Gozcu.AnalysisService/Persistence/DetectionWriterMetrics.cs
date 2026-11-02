namespace Gozcu.AnalysisService.Persistence;

/// <summary>Veritabanı yazıcısının sayaçları (durum API'si için).</summary>
public sealed class DetectionWriterMetrics
{
    private readonly object _lock = new();
    private long _framesWritten;
    private long _framesDropped;
    private long _eventsWritten;
    private long _eventsPublished;
    private long _publishFailures;
    private int _bufferedFrames;
    private int _pendingEvents;
    private DateTimeOffset? _lastWriteAt;
    private string? _lastError;

    public void FramesWritten(int count, DateTimeOffset at)
    {
        lock (_lock)
        {
            _framesWritten += count;
            _lastWriteAt = at;
        }
    }

    public void EventsWritten(int count, DateTimeOffset at)
    {
        lock (_lock)
        {
            _eventsWritten += count;
            _lastWriteAt = at;
        }
    }

    public void FrameDropped()
    {
        lock (_lock)
        {
            _framesDropped++;
        }
    }

    public void EventPublished()
    {
        lock (_lock)
        {
            _eventsPublished++;
        }
    }

    public void PublishFailed()
    {
        lock (_lock)
        {
            _publishFailures++;
        }
    }

    public void SetBacklog(int bufferedFrames, int pendingEvents)
    {
        lock (_lock)
        {
            _bufferedFrames = bufferedFrames;
            _pendingEvents = pendingEvents;
        }
    }

    public void SetError(string? error)
    {
        lock (_lock)
        {
            _lastError = error;
        }
    }

    public DetectionWriterSnapshot Snapshot(long queueDropped)
    {
        lock (_lock)
        {
            return new DetectionWriterSnapshot(
                _framesWritten, _framesDropped + queueDropped, _eventsWritten, _eventsPublished, _publishFailures,
                _bufferedFrames, _pendingEvents, _lastWriteAt, _lastError);
        }
    }
}

/// <param name="FramesDropped">Kuyruk ya da yazıcı tamponu dolu olduğu için atılan iş/kare sayısı.</param>
/// <param name="EventsWritten">Veritabanına yazılan olay satırı (açılış, güncelleme, kapanış) sayısı.</param>
/// <param name="BufferedFrames">Yazılmayı bekleyen kare sayısı.</param>
/// <param name="PendingEvents">Bellekte izlenen (açık ya da yazılmayı bekleyen) olay sayısı.</param>
public sealed record DetectionWriterSnapshot(
    long FramesWritten,
    long FramesDropped,
    long EventsWritten,
    long EventsPublished,
    long PublishFailures,
    int BufferedFrames,
    int PendingEvents,
    DateTimeOffset? LastWriteAt,
    string? LastError);
