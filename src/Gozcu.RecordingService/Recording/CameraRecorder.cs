using Gozcu.Media.Ffmpeg;
using Gozcu.Media.Processes;

namespace Gozcu.RecordingService.Recording;

/// <summary>
/// Bir kameranın ffmpeg kayıt sürecini izler. ffmpeg her parçayı bitirdiğinde stdout'a bir CSV
/// satırı yazar; bu satırdan parçanın dosyası, zamanı ve boyutu çıkarılıp kuyruğa konur.
/// </summary>
public sealed class CameraRecorder : ISupervisedProcessHandler
{
    private readonly object _lock = new();
    private readonly string _cameraId;
    private readonly string _directory;
    private readonly TimeSpan _segmentDuration;
    private readonly Action<CompletedSegment> _onSegment;
    private readonly TimeProvider _time;

    private RecorderState _state = RecorderState.Starting;
    private DateTimeOffset _stateSince;
    private int _segments;
    private DateTimeOffset? _lastSegmentAt;
    private int _restarts;
    private bool _started;
    private string? _lastError;
    private DateTimeOffset? _nextRetryAt;

    public CameraRecorder(string cameraId, string directory, TimeSpan segmentDuration, Action<CompletedSegment> onSegment, TimeProvider time)
    {
        _cameraId = cameraId;
        _directory = directory;
        _segmentDuration = segmentDuration;
        _onSegment = onSegment;
        _time = time;
        _stateSince = time.GetUtcNow();
    }

    /// <summary>Bu boyuttan küçük parçalar geçerli bir MP4 olamaz (bağlantı açılır açılmaz kopmuş).</summary>
    internal const long MinimumSegmentBytes = 1024;

    public void OnStarted()
    {
        RemoveTruncatedSegments();
        lock (_lock)
        {
            if (_started)
            {
                _restarts++;
            }

            _started = true;
            _nextRetryAt = null;
            SetState(RecorderState.Recording);
        }
    }

    public void OnStandardOutput(string line)
    {
        if (!SegmentListEntry.TryParse(line, out var entry) || entry is null)
        {
            return;
        }

        var path = Path.Combine(_directory, entry.FileName);
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return;
        }

        if (file.Length < MinimumSegmentBytes || entry.Duration <= TimeSpan.Zero)
        {
            TryDelete(path);
            return;
        }

        var now = _time.GetUtcNow();
        var startedAt = SegmentFileName.TryParse(entry.FileName, out _, out var nameStart)
            ? nameStart
            : now - entry.Duration;

        // Dosya adındaki zaman saniye hassasiyetinde, süre ise ffmpeg'in ölçtüğü akış süresidir.
        var duration = entry.Duration > TimeSpan.Zero ? entry.Duration : _segmentDuration;
        var segment = new CompletedSegment(_cameraId, path, startedAt, startedAt + duration, file.Length, now);

        lock (_lock)
        {
            _segments++;
            _lastSegmentAt = now;
            _lastError = null;
        }

        _onSegment(segment);
    }

    public void OnStandardError(string line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            lock (_lock)
            {
                _lastError = line.Trim();
            }
        }
    }

    public void OnExited(int? exitCode, Exception? error, TimeSpan retryDelay)
    {
        lock (_lock)
        {
            if (error is not null)
            {
                _lastError = error.Message;
            }

            _lastError ??= $"ffmpeg {exitCode} koduyla çıktı";
            _nextRetryAt = _time.GetUtcNow() + retryDelay;
            SetState(RecorderState.Retrying);
        }
    }

    public void OnStopped()
    {
        lock (_lock)
        {
            SetState(RecorderState.Stopped);
        }
    }

    public RecorderStatus Status()
    {
        lock (_lock)
        {
            return new RecorderStatus(_cameraId, _state, _stateSince, _segments, _lastSegmentAt, _restarts, _lastError, _nextRetryAt);
        }
    }

    /// <summary>Önceki süreç aniden kesildiyse geride kalan, içeriği olmayan parçaları temizler.</summary>
    private void RemoveTruncatedSegments()
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(_directory, "*" + SegmentFileName.Extension))
        {
            if (new FileInfo(path).Length < MinimumSegmentBytes)
            {
                TryDelete(path);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void SetState(RecorderState state)
    {
        if (_state != state)
        {
            _state = state;
            _stateSince = _time.GetUtcNow();
        }
    }
}
