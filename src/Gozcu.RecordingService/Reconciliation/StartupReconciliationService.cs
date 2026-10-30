using System.Collections.Concurrent;
using Gozcu.Contracts.Events;
using Gozcu.Infrastructure.Messaging;
using Gozcu.Infrastructure.Persistence;
using Gozcu.Media.Ffmpeg;
using Gozcu.Media.Probing;
using Gozcu.RecordingService.Recording;
using Microsoft.Extensions.Options;

namespace Gozcu.RecordingService.Reconciliation;

/// <summary>
/// Açılışta disk ile veritabanını uzlaştırır. Servis aniden kesildiyse (SIGKILL, elektrik kesintisi):
/// <list type="bullet">
/// <item>yazılmakta olan parçanın sonundaki moov atomu yazılmamıştır; dosya büyük ama oynatılamaz → karantina,</item>
/// <item>bitmiş ama bellekteki kuyrukta bekleyen parçalar veritabanına hiç yazılmamıştır → yeniden indekslenir,</item>
/// <item>dosyası dışarıdan silinmiş satırlar veritabanında kalmıştır → silindi olarak işaretlenir.</item>
/// </list>
/// </summary>
public sealed class StartupReconciliationService : BackgroundService
{
    /// <summary>
    /// Bozuk parçaların taşındığı dizin (RootPath altında). Dosya adı korunur; saklama servisi tüm alt dizinleri
    /// <see cref="SegmentFileName.TryParse"/> ile taradığı için karantinadaki dosyalar kotaya sayılır ve
    /// sırası geldiğinde en eskiler gibi silinir. Veritabanına yazılmadıkları için geri oynatmada görünmezler.
    /// </summary>
    public const string QuarantineDirectoryName = "_quarantine";

    /// <summary>Aynı anda çalışan ffprobe sayısı; kayıt süreçlerinin disk ve CPU payını korumak için düşük tutulur.</summary>
    internal const int ProbeParallelism = 4;

    private const int MarkDeletedBatchSize = 1000;

    private readonly RecordingOptions _options;
    private readonly IMediaProbe _probe;
    private readonly SegmentRepository _repository;
    private readonly DatabaseReadiness _readiness;
    private readonly IEventPublisher _publisher;
    private readonly ReconciliationReport _report;
    private readonly TimeProvider _time;
    private readonly ILogger<StartupReconciliationService> _logger;

    private IReadOnlyList<DiskSegment> _files = [];
    private DateTimeOffset _snapshotAt;
    private long _startedTimestamp;

    public StartupReconciliationService(
        IOptions<RecordingOptions> options,
        IMediaProbe probe,
        SegmentRepository repository,
        DatabaseReadiness readiness,
        IEventPublisher publisher,
        ReconciliationReport report,
        TimeProvider time,
        ILogger<StartupReconciliationService> logger)
    {
        _options = options.Value;
        _probe = probe;
        _repository = repository;
        _readiness = readiness;
        _publisher = publisher;
        _report = report;
        _time = time;
        _logger = logger;
    }

    private string Root => Path.GetFullPath(_options.RootPath);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Disk listesi burada, eşzamanlı olarak alınır. Bu servis Program.cs'te RecorderService'ten önce kayıtlıdır ve
        // .NET host'u barındırılan servisleri sırayla başlatır; yani bu noktada hiçbir ffmpeg süreci yeni bir parça
        // açmamıştır. Listedeki her dosya önceki çalışmalardan kalmadır ve hiçbiri şu an yazılmıyordur. Liste kayıt
        // başladıktan sonra alınsaydı, yazılmakta olan (henüz moov atomu olmayan) yeni parça bozuk sanılıp karantinaya
        // taşınabilirdi. Ağır işler (ffprobe, veritabanı) arka planda yapılır; açılışı ve kaydı geciktirmez.
        _startedTimestamp = _time.GetTimestamp();
        _snapshotAt = _time.GetUtcNow();
        _files = ScanRecordings(Root);
        var snapshotMs = _time.GetElapsedTime(_startedTimestamp).TotalMilliseconds;

        _report.Update(s => s with
        {
            State = ReconciliationState.WaitingForDatabase,
            StartedAt = _snapshotAt,
            Scanned = _files.Count,
            SnapshotMs = Math.Round(snapshotMs, 1),
        });
        _logger.LogInformation("Uzlaştırma: diskte önceki çalışmalardan {Count} parça bulundu ({Ms:0} ms)", _files.Count, snapshotMs);

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            // Hangi dosyanın incelenmesi gerektiğini veritabanı söyler; o yüzden disk işlemleri de veritabanını bekler.
            // Bu süre boyunca kayıt normal şekilde devam eder; bozuk dosya yalnızca biraz daha diskte kalır.
            var waitStarted = _time.GetTimestamp();
            await _readiness.WaitAsync(stoppingToken);
            var waitMs = _time.GetElapsedTime(waitStarted).TotalMilliseconds;
            _report.Update(s => s with { State = ReconciliationState.Running, DatabaseWaitMs = Math.Round(waitMs, 1) });

            var rows = await _repository.ListActiveAsync(stoppingToken);
            var disk = await ReconcileDiskAsync(_files, rows, _snapshotAt, stoppingToken);
            var (indexed, markedDeleted) = await ApplyToDatabaseAsync(disk, stoppingToken);

            var status = _report.Snapshot() with
            {
                State = ReconciliationState.Completed,
                CompletedAt = _time.GetUtcNow(),
                AlreadyIndexed = disk.Plan.AlreadyIndexed,
                Probed = disk.Probed,
                Valid = disk.Plan.AlreadyIndexed + disk.Plan.ToIndex.Count,
                Indexed = indexed,
                Quarantined = disk.Quarantined,
                RemovedTiny = disk.RemovedTiny,
                MarkedDeleted = markedDeleted,
                Skipped = disk.Skipped,
                ProbeMs = Math.Round(disk.ProbeMs, 1),
                TotalMs = Math.Round(_time.GetElapsedTime(_startedTimestamp).TotalMilliseconds, 1),
            };
            _report.Update(_ => status);

            _logger.LogInformation(
                "Uzlaştırma tamamlandı: {Scanned} parça tarandı, {Valid} geçerli ({AlreadyIndexed} zaten kayıtlı, {Probed} incelendi), " +
                "{Indexed} indekslendi, {Quarantined} karantinaya alındı, {RemovedTiny} boş parça silindi, " +
                "{MarkedDeleted} kayıp satır silindi olarak işaretlendi, {Skipped} atlandı ({TotalMs:0} ms)",
                status.Scanned, status.Valid, status.AlreadyIndexed, status.Probed, status.Indexed, status.Quarantined,
                status.RemovedTiny, status.MarkedDeleted, status.Skipped, status.TotalMs);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Tek seferlik bir iştir; yarıda kalırsa bir sonraki açılışta baştan yapılır (tüm adımlar idempotent).
            _logger.LogError(ex, "Uzlaştırma başarısız oldu");
            _report.Update(s => s with
            {
                State = ReconciliationState.Failed,
                CompletedAt = _time.GetUtcNow(),
                Error = ex.Message,
                TotalMs = Math.Round(_time.GetElapsedTime(_startedTimestamp).TotalMilliseconds, 1),
            });
        }
    }

    /// <summary>
    /// Kamera dizinlerindeki (RootPath/{kamera}/*.mp4) parçaları listeler. Karantina dizini dahil edilmez;
    /// oradaki dosyalar zaten incelenmiş ve ayrılmıştır.
    /// </summary>
    internal static List<DiskSegment> ScanRecordings(string root)
    {
        var result = new List<DiskSegment>();
        if (!Directory.Exists(root))
        {
            return result;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (string.Equals(Path.GetFileName(directory), QuarantineDirectoryName, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*" + SegmentFileName.Extension))
            {
                if (!SegmentFileName.TryParse(path, out var cameraId, out var startedAt))
                {
                    continue;
                }

                var info = new FileInfo(path);
                if (info.Exists)
                {
                    result.Add(new DiskSegment(path, cameraId, startedAt, info.Length, info.LastWriteTimeUtc));
                }
            }
        }

        return result;
    }

    /// <summary>Gerekli dosyaları inceler, planı çıkarır ve disk işlemlerini (silme, karantina) uygular.</summary>
    internal async Task<DiskOutcome> ReconcileDiskAsync(
        IReadOnlyList<DiskSegment> files,
        IReadOnlyCollection<RecordingSegment> activeRows,
        DateTimeOffset snapshotAt,
        CancellationToken cancellationToken)
    {
        var candidates = ReconciliationPlanner.ProbeCandidates(files, activeRows, CameraRecorder.MinimumSegmentBytes);

        var probeStarted = _time.GetTimestamp();
        var probes = new ConcurrentDictionary<string, MediaProbeResult>(StringComparer.Ordinal);
        var probeFailures = 0;
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions { MaxDegreeOfParallelism = ProbeParallelism, CancellationToken = cancellationToken },
            async (file, token) =>
            {
                try
                {
                    probes[file.FilePath] = await _probe.ProbeAsync(file.FilePath, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // ffprobe yoksa ya da yanıt vermiyorsa dosya hakkında hüküm verilemez; dokunulmaz.
                    if (Interlocked.Increment(ref probeFailures) == 1)
                    {
                        _logger.LogWarning("Parça incelenemedi ({File}): {Message}", file.FilePath, ex.Message);
                    }
                }
            });
        var probeMs = _time.GetElapsedTime(probeStarted).TotalMilliseconds;
        if (probeFailures > 1)
        {
            _logger.LogWarning("Toplam {Count} parça incelenemedi; bu parçalara dokunulmadı", probeFailures);
        }

        var plan = ReconciliationPlanner.Plan(files, probes, activeRows, snapshotAt, CameraRecorder.MinimumSegmentBytes, File.Exists);
        var skipped = plan.Undecided.Count;

        // Liste alındıktan sonra bir dosya değiştiyse (ör. yeni ffmpeg süreci aynı saniyede başlayıp aynı adla
        // yazmaya başladıysa) ona dokunulmaz: yazılmakta olan bir dosyayı taşımak o parçayı kaybettirir.
        var removedTiny = 0;
        foreach (var file in plan.ToDelete)
        {
            if (!File.Exists(file.FilePath))
            {
                // CameraRecorder da açılışta boş parçaları siler; ondan önce davrandıysa iş zaten yapılmıştır.
                removedTiny++;
            }
            else if (IsUnchanged(file) && TryDelete(file.FilePath))
            {
                removedTiny++;
            }
            else
            {
                skipped++;
            }
        }

        var quarantined = 0;
        foreach (var file in plan.ToQuarantine)
        {
            if (IsUnchanged(file) && TryQuarantine(file, probes[file.FilePath].Error))
            {
                quarantined++;
            }
            else
            {
                skipped++;
            }
        }

        var toIndex = new List<SegmentToIndex>(plan.ToIndex.Count);
        foreach (var segment in plan.ToIndex)
        {
            if (IsUnchanged(segment.File))
            {
                toIndex.Add(segment);
            }
            else
            {
                skipped++;
            }
        }

        return new DiskOutcome(plan, toIndex, candidates.Count, quarantined, removedTiny, skipped, probeMs);
    }

    /// <summary>Kayıp parçaları indeksler ve dosyası olmayan satırları silindi olarak işaretler.</summary>
    internal async Task<(int Indexed, int MarkedDeleted)> ApplyToDatabaseAsync(DiskOutcome disk, CancellationToken cancellationToken)
    {
        // Yeniden indekslenen parçalar SegmentQueue'ya konmaz, doğrudan yazılır: SegmentIndexer her parça için
        // "bitişten aranabilir olana kadar geçen süre"yi RecordingMetrics'e ekler; saatler önce bitmiş bir parça bu
        // ölçümü (README'deki gecikme tablosu) anlamsız şekilde büyütürdü.
        //
        // RecordingSegmentCompleted yine de yayınlanır: 3. aşamadaki analiz servisi bu olaydan beslenecek ve kesinti
        // sırasında kaybolan parçaları da görmesi gerekir. Aynı parça için olay birden fazla gelebilir: veritabanındaki
        // boyutu diskteki dosyadan farklı olan (önceden yayınlanmış) bir parça burada yeniden yayınlanır, aracı da
        // mesajı yineleyebilir. Tüketiciler SegmentId'ye göre tekilleştirmelidir (UpsertAsync aynı dosya için hep aynı
        // kimliği döner). Ters yönde bir boşluk kalır: indeksleyici satırı yazıp olayı yayınlayamadan servis kesilirse
        // satır diskteki boyutla aynı olduğundan burada yeniden yayınlanmaz. Bunu kapatmak için bir outbox tablosu gerekir.
        var indexed = 0;
        foreach (var segment in disk.ToIndex)
        {
            var file = segment.File;
            var id = await _repository.UpsertAsync(file.CameraId, file.FilePath, file.StartedAt, segment.EndedAt, file.SizeBytes, cancellationToken);
            indexed++;
            _logger.LogInformation(
                "Kayıp parça indekslendi: {CameraId} {File} ({Size:N0} bayt, {Duration:0.0} sn)",
                file.CameraId, Path.GetFileName(file.FilePath), file.SizeBytes, (segment.EndedAt - file.StartedAt).TotalSeconds);

            try
            {
                await _publisher.PublishAsync(
                    new RecordingSegmentCompleted(id, file.CameraId, file.FilePath, file.StartedAt, segment.EndedAt, file.SizeBytes),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Parça olayı yayınlanamadı: {Message}", ex.Message);
            }
        }

        // Son güvenlik kontrolü: dosyası hâlâ duran bir satır asla silindi olarak işaretlenmez
        // (ör. karantinaya taşıma başarısız olduysa).
        var missing = disk.Plan.ToMarkDeleted.Where(p => !File.Exists(p)).ToList();
        var now = _time.GetUtcNow();
        foreach (var batch in missing.Chunk(MarkDeletedBatchSize))
        {
            await _repository.MarkDeletedAsync(batch, now, cancellationToken);
        }

        if (missing.Count > 0)
        {
            _logger.LogInformation("Dosyası diskte olmayan {Count} parça veritabanında silindi olarak işaretlendi", missing.Count);
        }

        return (indexed, missing.Count);
    }

    private bool TryQuarantine(DiskSegment file, string? reason)
    {
        var directory = Path.Combine(Root, QuarantineDirectoryName, file.CameraId);
        var target = Path.Combine(directory, Path.GetFileName(file.FilePath));
        try
        {
            Directory.CreateDirectory(directory);
            File.Move(file.FilePath, target, overwrite: true);
            _logger.LogWarning(
                "Bozuk parça karantinaya alındı: {File} → {Target} ({Size:N0} bayt; {Reason})",
                file.FilePath, target, file.SizeBytes, reason ?? "oynatılamıyor");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("{File} karantinaya taşınamadı: {Message}", file.FilePath, ex.Message);
            return false;
        }
    }

    private bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("{File} silinemedi: {Message}", path, ex.Message);
            return false;
        }
    }

    private static bool IsUnchanged(DiskSegment file)
    {
        var info = new FileInfo(file.FilePath);
        return info.Exists && info.Length == file.SizeBytes && info.LastWriteTimeUtc == file.LastWriteTimeUtc;
    }
}

/// <summary>Disk adımının sonucu; <see cref="ToIndex"/> yalnızca bu arada değişmemiş dosyaları içerir.</summary>
public sealed record DiskOutcome(
    ReconciliationPlan Plan,
    IReadOnlyList<SegmentToIndex> ToIndex,
    int Probed,
    int Quarantined,
    int RemovedTiny,
    int Skipped,
    double ProbeMs);
