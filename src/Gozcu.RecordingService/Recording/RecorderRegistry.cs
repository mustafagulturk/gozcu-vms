using System.Collections.Concurrent;

namespace Gozcu.RecordingService.Recording;

public sealed class RecorderRegistry
{
    private readonly ConcurrentDictionary<string, CameraRecorder> _recorders = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string cameraId, CameraRecorder recorder) => _recorders[cameraId] = recorder;

    public IEnumerable<RecorderStatus> Statuses() =>
        _recorders.Values.Select(r => r.Status()).OrderBy(s => s.CameraId, StringComparer.Ordinal);
}
