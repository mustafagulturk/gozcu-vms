using Gozcu.Infrastructure.Persistence;
using Gozcu.Media.Ffmpeg;
using Microsoft.Extensions.Options;

namespace Gozcu.RecordingService.Retention;

/// <summary>
/// Diski periyodik olarak tarar; kota aşıldığında veya disk dolmak üzereyken en eski parçaları siler.
/// Kaynak olarak dosya sistemi kullanılır, böylece veritabanı erişilemezken de disk dolmaz.
/// </summary>
public sealed class RetentionService : BackgroundService
{
    private readonly RecordingOptions _options;
    private readonly SegmentRepository _repository;
    private readonly DatabaseReadiness _readiness;
    private readonly TimeProvider _time;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(
        IOptions<RecordingOptions> options,
        SegmentRepository repository,
        DatabaseReadiness readiness,
        TimeProvider time,
        ILogger<RetentionService> logger)
    {
        _options = options.Value;
        _repository = repository;
        _readiness = readiness;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.Retention.CheckInterval, _time);
        try
        {
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Saklama politikası uygulanamadı");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task<IReadOnlyList<StoredSegment>> RunOnceAsync(CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(_options.RootPath);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var segments = ScanSegments(root);
        var plan = RetentionPlanner.Plan(segments, new DriveInfo(root).AvailableFreeSpace, _options.Retention, NewestPerCamera(segments));
        if (plan.Count == 0)
        {
            return plan;
        }

        var deleted = new List<string>();
        foreach (var segment in plan)
        {
            try
            {
                File.Delete(segment.FilePath);
                deleted.Add(segment.FilePath);
            }
            catch (IOException ex)
            {
                _logger.LogWarning("{File} silinemedi: {Message}", segment.FilePath, ex.Message);
            }
        }

        _logger.LogInformation(
            "Saklama politikası: {Count} eski parça silindi ({Bytes:N0} bayt)",
            deleted.Count, plan.Where(p => deleted.Contains(p.FilePath)).Sum(p => p.SizeBytes));

        if (_readiness.IsReady)
        {
            await _repository.MarkDeletedAsync(deleted, _time.GetUtcNow(), cancellationToken);
        }

        return plan;
    }

    internal static List<StoredSegment> ScanSegments(string root)
    {
        var result = new List<StoredSegment>();
        foreach (var path in Directory.EnumerateFiles(root, "*" + SegmentFileName.Extension, SearchOption.AllDirectories))
        {
            if (SegmentFileName.TryParse(path, out var cameraId, out var startedAt))
            {
                result.Add(new StoredSegment(path, cameraId, startedAt, new FileInfo(path).Length));
            }
        }

        return result;
    }

    /// <summary>Her kameranın en yeni parçası hâlâ yazılıyor olabilir; asla silinmez.</summary>
    internal static HashSet<string> NewestPerCamera(IEnumerable<StoredSegment> segments) =>
        segments.GroupBy(s => s.CameraId)
            .Select(g => g.MaxBy(s => s.StartedAt)!.FilePath)
            .ToHashSet(StringComparer.Ordinal);
}
