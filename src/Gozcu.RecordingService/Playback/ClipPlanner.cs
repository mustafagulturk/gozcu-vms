using System.Globalization;
using Gozcu.Infrastructure.Persistence;
using Gozcu.Media.Ffmpeg;

namespace Gozcu.RecordingService.Playback;

/// <summary>
/// Dışa aktarılacak klip için ffmpeg concat listesini planlar: aralıkla kesişen parçalar, ilk parça için
/// "inpoint", son parça için "outpoint" (her ikisi de parçanın başına göre). Saf fonksiyonlardır.
/// </summary>
public static class ClipPlanner
{
    /// <summary>Aralığı doğrular; geçersizse kullanıcıya gösterilecek hata mesajını döner.</summary>
    public static string? Validate(DateTimeOffset from, DateTimeOffset to, TimeSpan maxDuration)
    {
        if (to <= from)
        {
            return "Bitiş zamanı başlangıçtan sonra olmalı.";
        }

        if (to - from > maxDuration)
        {
            return $"Klip en fazla {FormatDuration(maxDuration)} olabilir.";
        }

        return null;
    }

    /// <summary>
    /// Aralığı kapsayan concat girdilerini üretir. Aralıkla kesişen parça yoksa boş liste döner.
    /// Üst üste binen parçalarda (ör. yeniden başlatma sonrası) önceki parçanın kapsadığı kısım atlanır.
    /// </summary>
    /// <param name="resolvePath">Veritabanındaki yolu dosya sistemindeki tam yola çevirir; <c>null</c> dönerse parça atlanır.</param>
    public static IReadOnlyList<ConcatEntry> Plan(
        IEnumerable<RecordingSegment> segments,
        DateTimeOffset from,
        DateTimeOffset to,
        Func<RecordingSegment, string?>? resolvePath = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (to <= from)
        {
            throw new ArgumentException("Bitiş zamanı başlangıçtan sonra olmalı.", nameof(to));
        }

        var entries = new List<ConcatEntry>();
        var covered = from;
        foreach (var segment in segments.OrderBy(s => s.StartedAt).ThenBy(s => s.Id))
        {
            if (segment.EndedAt <= covered || segment.StartedAt >= to || segment.EndedAt <= segment.StartedAt)
            {
                continue;
            }

            var path = resolvePath is null ? segment.FilePath : resolvePath(segment);
            if (path is null)
            {
                continue;
            }

            var start = covered > segment.StartedAt ? covered : segment.StartedAt;
            var end = to < segment.EndedAt ? to : segment.EndedAt;
            entries.Add(new ConcatEntry(
                path,
                start > segment.StartedAt ? start - segment.StartedAt : null,
                end < segment.EndedAt ? end - segment.StartedAt : null));
            covered = end;
        }

        return entries;
    }

    /// <summary>İndirilen dosyanın adı: "cam1_20261030T101500Z-20261030T102000Z.mp4".</summary>
    public static string FileName(string cameraId, DateTimeOffset from, DateTimeOffset to) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Path.GetFileNameWithoutExtension(SegmentFileName.Format(cameraId, from))}-{to.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}{SegmentFileName.Extension}");

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalMinutes >= 60 && duration.Minutes == 0 && duration.Seconds == 0
            ? $"{(int)duration.TotalHours} saat"
            : duration.TotalSeconds >= 60 && duration.Seconds == 0
                ? $"{(int)duration.TotalMinutes} dakika"
                : $"{(int)duration.TotalSeconds} saniye";
}
