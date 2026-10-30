using Gozcu.Infrastructure.Persistence;
using Gozcu.Media.Probing;

namespace Gozcu.RecordingService.Reconciliation;

/// <summary>Açılış anında diskte bulunan bir kayıt parçası.</summary>
/// <param name="LastWriteTimeUtc">Dosyaya işlem yapmadan önce, o arada değişmediğini doğrulamak için tutulur.</param>
public sealed record DiskSegment(string FilePath, string CameraId, DateTimeOffset StartedAt, long SizeBytes, DateTime LastWriteTimeUtc);

/// <summary>Veritabanına eklenecek (ya da boyutu düzeltilecek) geçerli bir parça.</summary>
public sealed record SegmentToIndex(DiskSegment File, DateTimeOffset EndedAt);

/// <summary>Uzlaştırma kararı. Sırası: önce disk işlemleri, sonra veritabanı.</summary>
public sealed record ReconciliationPlan(
    IReadOnlyList<SegmentToIndex> ToIndex,
    IReadOnlyList<DiskSegment> ToQuarantine,
    IReadOnlyList<DiskSegment> ToDelete,
    IReadOnlyList<string> ToMarkDeleted,
    int AlreadyIndexed,
    IReadOnlyList<DiskSegment> Undecided);

/// <summary>
/// Açılışta disk ile veritabanını karşılaştırıp ne yapılacağına karar verir. Saf fonksiyondur;
/// dosya sistemine ve veritabanına dokunmaz (dosyanın varlığı dışarıdan verilen bir fonksiyonla sorulur).
/// </summary>
public static class ReconciliationPlanner
{
    /// <summary>
    /// ffprobe ile incelenmesi gereken dosyalar: veritabanında olmayanlar ya da boyutu farklı olanlar.
    /// Veritabanındaki boyutla aynı olan bir dosyayı ffmpeg zaten düzgün kapatmış ve indeksleyici yazmıştır;
    /// yeniden incelenmez. Böylece on binlerce parçalık bir arşivde açılış maliyeti birkaç dosyayla sınırlı kalır.
    /// </summary>
    public static IReadOnlyList<DiskSegment> ProbeCandidates(
        IReadOnlyCollection<DiskSegment> files,
        IReadOnlyCollection<RecordingSegment> activeRows,
        long minimumBytes)
    {
        ArgumentNullException.ThrowIfNull(files);
        var rows = ByPath(activeRows);
        return files
            .Where(f => f.SizeBytes >= minimumBytes && !(rows.TryGetValue(f.FilePath, out var row) && row.SizeBytes == f.SizeBytes))
            .ToList();
    }

    /// <param name="files">Kayıt süreçleri başlamadan önce alınan disk listesi.</param>
    /// <param name="probes">İncelenen dosyaların sonuçları (yola göre). Sonucu olmayan aday dosyaya dokunulmaz.</param>
    /// <param name="activeRows">Veritabanındaki silinmemiş parçalar.</param>
    /// <param name="snapshotAt">Disk listesinin alındığı an. Bundan sonra başlayan parçalar bu çalışmaya aittir.</param>
    /// <param name="minimumBytes">Bundan küçük dosyalar geçerli MP4 olamaz.</param>
    /// <param name="fileExists">Disk listesinde olmayan bir veritabanı satırının dosyası hâlâ var mı.</param>
    public static ReconciliationPlan Plan(
        IReadOnlyCollection<DiskSegment> files,
        IReadOnlyDictionary<string, MediaProbeResult> probes,
        IReadOnlyCollection<RecordingSegment> activeRows,
        DateTimeOffset snapshotAt,
        long minimumBytes,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(fileExists);

        var rows = ByPath(activeRows);
        var toIndex = new List<SegmentToIndex>();
        var toQuarantine = new List<DiskSegment>();
        var toDelete = new List<DiskSegment>();
        var toMarkDeleted = new List<string>();
        var undecided = new List<DiskSegment>();
        var alreadyIndexed = 0;

        var onDisk = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files.OrderBy(f => f.StartedAt).ThenBy(f => f.FilePath, StringComparer.Ordinal))
        {
            onDisk.Add(file.FilePath);
            var hasRow = rows.TryGetValue(file.FilePath, out var row);

            if (file.SizeBytes < minimumBytes)
            {
                toDelete.Add(file);
                if (hasRow)
                {
                    toMarkDeleted.Add(file.FilePath);
                }

                continue;
            }

            if (hasRow && row!.SizeBytes == file.SizeBytes)
            {
                alreadyIndexed++;
                continue;
            }

            if (!probes.TryGetValue(file.FilePath, out var probe))
            {
                // İncelenemedi (ffprobe çalışmadı, zaman aşımı): bozuk sayıp karantinaya almak veri kaybı riski taşır.
                undecided.Add(file);
                continue;
            }

            if (probe.IsPlayable)
            {
                toIndex.Add(new SegmentToIndex(file, file.StartedAt + probe.Duration));
            }
            else
            {
                toQuarantine.Add(file);
                if (hasRow)
                {
                    toMarkDeleted.Add(file.FilePath);
                }
            }
        }

        foreach (var row in rows.Values.OrderBy(r => r.StartedAt).ThenBy(r => r.FilePath, StringComparer.Ordinal))
        {
            // Listeden sonra başlayan parçalar bu çalışmanın kayıt süreçlerine aittir; onlara dokunulmaz.
            if (onDisk.Contains(row.FilePath) || row.StartedAt >= snapshotAt)
            {
                continue;
            }

            if (!fileExists(row.FilePath))
            {
                toMarkDeleted.Add(row.FilePath);
            }
        }

        return new ReconciliationPlan(toIndex, toQuarantine, toDelete, toMarkDeleted, alreadyIndexed, undecided);
    }

    private static Dictionary<string, RecordingSegment> ByPath(IReadOnlyCollection<RecordingSegment> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var result = new Dictionary<string, RecordingSegment>(rows.Count, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            result[row.FilePath] = row;
        }

        return result;
    }
}
