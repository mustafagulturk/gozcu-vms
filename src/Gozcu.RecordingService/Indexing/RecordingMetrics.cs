namespace Gozcu.RecordingService.Indexing;

/// <summary>
/// Kayıt gecikmesi: bir parçanın bitiş anından veritabanında aranabilir olmasına kadar geçen süre.
/// README'deki ölçüm tablosu için toplanır.
/// </summary>
public sealed class RecordingMetrics
{
    private readonly object _lock = new();
    private long _count;
    private double _totalMs;
    private double _maxMs;
    private double? _lastMs;

    public void RecordIndexLatency(TimeSpan latency)
    {
        var ms = Math.Max(0, latency.TotalMilliseconds);
        lock (_lock)
        {
            _count++;
            _totalMs += ms;
            _maxMs = Math.Max(_maxMs, ms);
            _lastMs = ms;
        }
    }

    public RecordingMetricsSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new RecordingMetricsSnapshot(
                _count,
                _lastMs is null ? null : Math.Round(_lastMs.Value, 1),
                _count == 0 ? null : Math.Round(_totalMs / _count, 1),
                _count == 0 ? null : Math.Round(_maxMs, 1));
        }
    }
}

public sealed record RecordingMetricsSnapshot(long IndexedSegments, double? LastLatencyMs, double? AverageLatencyMs, double? MaxLatencyMs);
