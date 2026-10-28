using Gozcu.Media.Ffmpeg;
using Gozcu.Media.Processes;

namespace Gozcu.StreamingService.Health;

/// <summary>Bir kameranın ffmpeg izleme sürecinin çıktısını sağlık takipçisine aktarır.</summary>
public sealed class CameraMonitor : ISupervisedProcessHandler
{
    private readonly CameraHealthTracker _tracker;
    private readonly Action<CameraStateTransition> _onTransition;
    private FfmpegProgressParser _parser = new();

    public CameraMonitor(CameraHealthTracker tracker, Action<CameraStateTransition> onTransition)
    {
        _tracker = tracker;
        _onTransition = onTransition;
    }

    public void OnStarted()
    {
        _parser = new FfmpegProgressParser();
        Raise(_tracker.OnConnecting());
    }

    public void OnStandardOutput(string line)
    {
        if (_parser.TryAccept(line, out var progress) && progress is not null)
        {
            Raise(_tracker.OnProgress(progress));
        }
    }

    public void OnStandardError(string line)
    {
        if (StreamInfo.TryParse(line, out var info) && info is not null)
        {
            _tracker.OnStreamInfo(info);
        }
        else if (LooksLikeError(line))
        {
            _tracker.OnErrorLine(line.Trim());
        }
    }

    public void OnExited(int? exitCode, Exception? error, TimeSpan retryDelay)
    {
        if (error is not null)
        {
            _tracker.OnErrorLine(error.Message);
        }

        Raise(_tracker.OnDisconnected(exitCode, retryDelay));
    }

    public void OnStopped()
    {
    }

    private void Raise(CameraStateTransition? transition)
    {
        if (transition is not null)
        {
            _onTransition(transition);
        }
    }

    private static bool LooksLikeError(string line) =>
        line.Contains("error", StringComparison.OrdinalIgnoreCase)
        || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
        || line.Contains("timed out", StringComparison.OrdinalIgnoreCase)
        || line.Contains("refused", StringComparison.OrdinalIgnoreCase)
        || line.Contains("not found", StringComparison.OrdinalIgnoreCase);
}
