namespace Gozcu.RecordingService.Retention;

/// <summary>Diskteki bir kayıt parçası.</summary>
public sealed record StoredSegment(string FilePath, string CameraId, DateTimeOffset StartedAt, long SizeBytes);

/// <summary>
/// Saklama politikası: toplam boyut sınırı aşıldığında ya da boş alan eşiğin altına düştüğünde
/// en eski parçalardan başlayarak silinecekleri seçer. Saf fonksiyondur; dosya sistemine dokunmaz.
/// </summary>
public static class RetentionPlanner
{
    /// <param name="segments">Diskteki tüm parçalar.</param>
    /// <param name="freeBytes">Diskte şu an boş olan alan.</param>
    /// <param name="protectedPaths">Silinmemesi gereken dosyalar (ör. yazılmakta olan son parça).</param>
    public static IReadOnlyList<StoredSegment> Plan(
        IReadOnlyCollection<StoredSegment> segments,
        long freeBytes,
        RetentionOptions options,
        IReadOnlySet<string> protectedPaths)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(protectedPaths);

        var total = segments.Sum(s => s.SizeBytes);
        var toDelete = new List<StoredSegment>();

        foreach (var segment in segments.OrderBy(s => s.StartedAt).ThenBy(s => s.FilePath, StringComparer.Ordinal))
        {
            var overQuota = options.MaxTotalBytes > 0 && total > options.MaxTotalBytes;
            var lowDisk = freeBytes < options.MinFreeBytes;
            if (!overQuota && !lowDisk)
            {
                break;
            }

            if (protectedPaths.Contains(segment.FilePath))
            {
                continue;
            }

            toDelete.Add(segment);
            total -= segment.SizeBytes;
            freeBytes += segment.SizeBytes;
        }

        return toDelete;
    }
}
