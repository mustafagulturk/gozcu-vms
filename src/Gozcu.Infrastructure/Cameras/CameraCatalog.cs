using Gozcu.Contracts.Cameras;
using Gozcu.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Gozcu.Infrastructure.Cameras;

/// <summary>"CameraCatalog" yapılandırma bölümü.</summary>
public sealed class CameraCatalogOptions
{
    public const string SectionName = "CameraCatalog";

    /// <summary>
    /// Olay gelmese de kataloğun veritabanından yeniden okunma aralığı. RabbitMQ olayı kaybolursa (aracı kapalıyken
    /// yapılan değişiklik, kopan bağlantı) fark en geç bu süre sonunda kapanır.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Okuma başarısız olursa yeniden deneme aralığı.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// <see cref="ICameraCatalog"/> uygulaması: veritabanındaki kamera listesinin değişmez bir kopyasını tutar.
/// Yenileme yalnızca içerik gerçekten değiştiğinde sürümü artırır; böylece periyodik yenileme gereksiz yere
/// kamera süreçlerini yeniden başlatmaz.
/// </summary>
public sealed class CameraCatalog : ICameraCatalog, IDisposable
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<CameraDefinition>>> _load;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lock = new();

    private IReadOnlyList<CameraDefinition> _cameras = [];
    private Dictionary<string, CameraDefinition> _byId = new(StringComparer.OrdinalIgnoreCase);
    private long _version;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CameraCatalog(CameraRepository repository, DatabaseReadiness readiness, ILogger<CameraCatalog> logger)
        : this(
            async cancellationToken =>
            {
                // Şema uygulanmadan tablo okunamaz.
                await readiness.WaitAsync(cancellationToken).ConfigureAwait(false);
                var rows = await repository.ListAsync(cancellationToken).ConfigureAwait(false);
                return rows.Select(r => r.ToDefinition()).ToList();
            },
            logger)
    {
    }

    /// <summary>Kaynağı dışarıdan verilen katalog (birim testler için).</summary>
    internal CameraCatalog(Func<CancellationToken, Task<IReadOnlyList<CameraDefinition>>> load, ILogger logger)
    {
        _load = load;
        _logger = logger;
    }

    public IReadOnlyList<CameraDefinition> Cameras
    {
        get
        {
            lock (_lock)
            {
                return _cameras;
            }
        }
    }

    public long Version
    {
        get
        {
            lock (_lock)
            {
                return _version;
            }
        }
    }

    public bool IsLoaded => _loaded.Task.IsCompleted;

    public CameraDefinition? Find(string cameraId)
    {
        lock (_lock)
        {
            return _byId.GetValueOrDefault(cameraId);
        }
    }

    public Task WaitUntilLoadedAsync(CancellationToken cancellationToken) => _loaded.Task.WaitAsync(cancellationToken);

    public async Task<long> WaitForChangeAsync(long knownVersion, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                if (_version != knownVersion)
                {
                    return _version;
                }

                changed = _changed.Task;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        // Eşzamanlı yenilemeler sıraya girer: API yazması ile RabbitMQ olayı aynı anda yenileme isteyebilir.
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cameras = await _load(cancellationToken).ConfigureAwait(false);
            Apply(cameras);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Yeni listeyi uygular. İçerik değiştiyse (ya da ilk yüklemeyse) sürümü artırır ve true döner.</summary>
    internal bool Apply(IEnumerable<CameraDefinition> cameras)
    {
        var next = cameras
            .Select(Copy)
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToList();

        TaskCompletionSource changed;
        long version;
        string summary;
        lock (_lock)
        {
            if (_version > 0 && SameContent(_cameras, next))
            {
                return false;
            }

            summary = Describe(_cameras, next);
            _cameras = next;
            _byId = next.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            version = ++_version;
            changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _logger.LogInformation("Kamera kataloğu güncellendi (sürüm {Version}): {Summary}", version, summary);
        changed.TrySetResult();
        _loaded.TrySetResult();
        return true;
    }

    public void Dispose() => _refreshGate.Dispose();

    /// <summary>Tanımın kopyası; katalog dışarıdan değiştirilemez.</summary>
    private static CameraDefinition Copy(CameraDefinition camera) => new()
    {
        Id = camera.Id,
        Name = camera.Name,
        RtspUrl = camera.RtspUrl,
        Enabled = camera.Enabled,
    };

    internal static bool SameDefinition(CameraDefinition a, CameraDefinition b) =>
        string.Equals(a.Id, b.Id, StringComparison.Ordinal)
        && string.Equals(a.Name, b.Name, StringComparison.Ordinal)
        && string.Equals(a.RtspUrl?.OriginalString, b.RtspUrl?.OriginalString, StringComparison.Ordinal)
        && a.Enabled == b.Enabled;

    private static bool SameContent(IReadOnlyList<CameraDefinition> current, IReadOnlyList<CameraDefinition> next) =>
        current.Count == next.Count && current.Zip(next).All(p => SameDefinition(p.First, p.Second));

    private static string Describe(IReadOnlyList<CameraDefinition> current, IReadOnlyList<CameraDefinition> next)
    {
        var before = current.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var after = next.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var added = after.Keys.Where(id => !before.ContainsKey(id)).ToList();
        var removed = before.Keys.Where(id => !after.ContainsKey(id)).ToList();
        var changed = after.Values.Where(c => before.TryGetValue(c.Id, out var old) && !SameDefinition(old, c)).Select(c => c.Id).ToList();
        return $"{next.Count} kamera ({next.Count(c => c.Enabled)} etkin); eklenen [{string.Join(", ", added)}], "
            + $"silinen [{string.Join(", ", removed)}], değişen [{string.Join(", ", changed)}]";
    }
}
