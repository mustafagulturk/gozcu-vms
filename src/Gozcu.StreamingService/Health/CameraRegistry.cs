using System.Collections.Concurrent;
using Gozcu.Contracts.Cameras;
using Microsoft.Extensions.Options;

namespace Gozcu.StreamingService.Health;

/// <summary>Yapılandırılmış kameraların sağlık takipçileri.</summary>
public sealed class CameraRegistry
{
    private readonly ConcurrentDictionary<string, CameraHealthTracker> _trackers = new(StringComparer.OrdinalIgnoreCase);

    public CameraRegistry(IOptions<CameraOptions> cameras, IOptions<StreamingOptions> options, TimeProvider time)
    {
        foreach (var camera in cameras.Value.Enabled)
        {
            _trackers[camera.Id] = new CameraHealthTracker(camera, options.Value, time);
        }
    }

    public IEnumerable<CameraHealthTracker> All => _trackers.Values.OrderBy(t => t.CameraId, StringComparer.Ordinal);

    public CameraHealthTracker? Find(string cameraId) => _trackers.GetValueOrDefault(cameraId);
}
