using System.Collections.Concurrent;
using Gozcu.Contracts.Cameras;
using Microsoft.Extensions.Options;

namespace Gozcu.StreamingService.Health;

/// <summary>İzlenmekte olan kameraların sağlık takipçileri. Kamera kataloğu değiştikçe eklenir ve çıkarılır.</summary>
public sealed class CameraRegistry
{
    private readonly ConcurrentDictionary<string, CameraHealthTracker> _trackers = new(StringComparer.OrdinalIgnoreCase);
    private readonly StreamingOptions _options;
    private readonly TimeProvider _time;

    public CameraRegistry(IOptions<StreamingOptions> options, TimeProvider time)
    {
        _options = options.Value;
        _time = time;
    }

    public IEnumerable<CameraHealthTracker> All => _trackers.Values.OrderBy(t => t.CameraId, StringComparer.Ordinal);

    public CameraHealthTracker? Find(string cameraId) => _trackers.GetValueOrDefault(cameraId);

    /// <summary>Kamera için yeni bir takipçi oluşturur; aynı kimlikte eskisi varsa yerini alır.</summary>
    public CameraHealthTracker Add(CameraDefinition camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        var tracker = new CameraHealthTracker(camera, _options, _time);
        _trackers[camera.Id] = tracker;
        return tracker;
    }

    /// <summary>Takipçiyi çıkarır; bu arada aynı kimlikle yenisi eklendiyse ona dokunmaz.</summary>
    public bool Remove(CameraHealthTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        return _trackers.TryRemove(KeyValuePair.Create(tracker.CameraId, tracker));
    }
}
