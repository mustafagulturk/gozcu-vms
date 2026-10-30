using Gozcu.Contracts.Cameras;
using Gozcu.Media.Ffmpeg;

namespace Gozcu.StreamingService.Health;

/// <summary>
/// Bir kameranın sağlık durum makinesi.
/// Connecting → Online (medya akmaya başladı) → Degraded (akış yavaşladı/durdu) → Offline (bağlantı koptu) → Connecting ...
/// Tüm kararlar <see cref="TimeProvider"/> üzerinden verildiği için birim testlerde zaman kontrol edilebilir.
/// </summary>
public sealed class CameraHealthTracker
{
    private const double RateSmoothing = 0.3;

    private readonly object _lock = new();
    private readonly CameraDefinition _camera;
    private readonly StreamingOptions _options;
    private readonly TimeProvider _time;
    private readonly Uri? _hlsUrl;
    private readonly Uri? _playbackUrl;

    private CameraHealthState _state = CameraHealthState.Connecting;
    private DateTimeOffset _stateSince;
    private DateTimeOffset? _lastMediaAt;
    private TimeSpan? _lastOutTime;
    private DateTimeOffset? _lastProgressAt;
    private double? _rate;
    private StreamInfo? _stream;
    private int _reconnectCount;
    private bool _everConnected;
    private string? _lastError;
    private DateTimeOffset? _nextRetryAt;

    public CameraHealthTracker(CameraDefinition camera, StreamingOptions options, TimeProvider time)
    {
        _camera = camera;
        _options = options;
        _time = time;
        _stateSince = time.GetUtcNow();
        _hlsUrl = new Uri(options.PublicHlsBaseUrl, $"{camera.Id}/index.m3u8");
        _playbackUrl = options.PublicPlaybackUrl is null ? null : new Uri(options.PublicPlaybackUrl, $"?camera={camera.Id}");
    }

    public string CameraId => _camera.Id;

    public CameraHealthState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    public CameraStateTransition? OnConnecting()
    {
        lock (_lock)
        {
            _lastOutTime = null;
            _lastProgressAt = null;
            _rate = null;
            _nextRetryAt = null;
            if (_everConnected)
            {
                _reconnectCount++;
            }

            return Transition(CameraHealthState.Connecting, "Bağlantı kuruluyor");
        }
    }

    public void OnStreamInfo(StreamInfo info)
    {
        lock (_lock)
        {
            _stream = info;
        }
    }

    public void OnErrorLine(string line)
    {
        lock (_lock)
        {
            _lastError = line;
        }
    }

    public CameraStateTransition? OnProgress(FfmpegProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (progress.OutTime is not { } outTime)
            {
                return null;
            }

            if (_lastOutTime is { } previousOut && _lastProgressAt is { } previousAt && outTime > previousOut)
            {
                var wall = (now - previousAt).TotalSeconds;
                if (wall > 0)
                {
                    var instant = (outTime - previousOut).TotalSeconds / wall;
                    _rate = _rate is null ? instant : (RateSmoothing * instant) + ((1 - RateSmoothing) * _rate.Value);
                }
            }

            if (_lastOutTime is null || outTime > _lastOutTime)
            {
                _lastMediaAt = now;
                _everConnected = true;
            }

            _lastOutTime = outTime;
            _lastProgressAt = now;
            return EvaluateLocked(now);
        }
    }

    public CameraStateTransition? OnDisconnected(int? exitCode, TimeSpan retryDelay)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            _nextRetryAt = now + retryDelay;
            _rate = null;
            var reason = _lastError ?? (exitCode is null ? "ffmpeg başlatılamadı" : $"ffmpeg {exitCode} koduyla çıktı");
            return Transition(CameraHealthState.Offline, reason);
        }
    }

    /// <summary>Periyodik olarak çağrılır; akış durduysa Degraded'a geçirir.</summary>
    public CameraStateTransition? Evaluate()
    {
        lock (_lock)
        {
            return EvaluateLocked(_time.GetUtcNow());
        }
    }

    public CameraHealthSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new CameraHealthSnapshot(
                _camera.Id, _camera.Name, _state, _stateSince, _lastMediaAt,
                _rate is null ? null : Math.Round(_rate.Value, 2),
                _stream, _reconnectCount, _lastError, _nextRetryAt, _hlsUrl, _playbackUrl);
        }
    }

    private CameraStateTransition? EvaluateLocked(DateTimeOffset now)
    {
        if (_state is CameraHealthState.Offline || _lastMediaAt is null || _lastProgressAt is null)
        {
            return null;
        }

        // Bu bağlantıda henüz medya gelmediyse Connecting'de kal.
        if (_state is CameraHealthState.Connecting && _lastOutTime is null)
        {
            return null;
        }

        var sinceMedia = now - _lastMediaAt.Value;
        if (sinceMedia > _options.StallTimeout)
        {
            return Transition(CameraHealthState.Degraded, $"{sinceMedia.TotalSeconds:0} saniyedir görüntü gelmiyor");
        }

        if (_rate is { } rate && rate < _options.DegradedRateThreshold)
        {
            return Transition(CameraHealthState.Degraded, $"Akış gerçek zamanın gerisinde (oran {rate:0.00})");
        }

        return Transition(CameraHealthState.Online, null);
    }

    private CameraStateTransition? Transition(CameraHealthState next, string? reason)
    {
        if (next == _state)
        {
            return null;
        }

        var previous = _state;
        _state = next;
        _stateSince = _time.GetUtcNow();
        if (next is CameraHealthState.Online)
        {
            _lastError = null;
        }

        return new CameraStateTransition(_camera.Id, previous, next, reason);
    }
}
