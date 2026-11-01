using System.Collections.Concurrent;

namespace Gozcu.RecordingService.Recording;

/// <summary>Çalışan kayıt süreçleri. Kamera kataloğu değiştikçe eklenir ve çıkarılır.</summary>
public sealed class RecorderRegistry
{
    private readonly ConcurrentDictionary<string, CameraRecorder> _recorders = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string cameraId, CameraRecorder recorder) => _recorders[cameraId] = recorder;

    /// <summary>Kaydediciyi çıkarır; bu arada aynı kimlikle yenisi eklendiyse ona dokunmaz.</summary>
    public bool Remove(string cameraId, CameraRecorder recorder) =>
        _recorders.TryRemove(KeyValuePair.Create(cameraId, recorder));

    public IEnumerable<RecorderStatus> Statuses() =>
        _recorders.Values.Select(r => r.Status()).OrderBy(s => s.CameraId, StringComparer.Ordinal);
}
