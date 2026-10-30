using Gozcu.Infrastructure.Persistence;

namespace Gozcu.RecordingService.Playback;

/// <summary>Bir zaman aralığı (başlangıç dahil, bitiş hariç).</summary>
public sealed record TimeRange(DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>Zaman çizelgesindeki bir kayıt parçası; parçanın kendi (kırpılmamış) başlangıç ve bitişiyle.</summary>
public sealed record TimelineItem(long SegmentId, DateTimeOffset Start, DateTimeOffset End);

/// <summary>İstenen aralığın geri oynatma görünümü: parçalar, boşluklar ve kayıtlı kısımlar.</summary>
/// <param name="Items">Başlangıca göre sıralı parçalar.</param>
/// <param name="Gaps">İstenen aralık içindeki kaydı olmayan kısımlar.</param>
/// <param name="Coverage">İstenen aralığa kırpılmış, birleştirilmiş kayıtlı kısımlar.</param>
public sealed record PlaybackTimeline(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<TimelineItem> Items,
    IReadOnlyList<TimeRange> Gaps,
    IReadOnlyList<TimeRange> Coverage)
{
    public double RecordedSeconds => Coverage.Sum(c => c.Duration.TotalSeconds);
}

/// <summary>Bir an için oynatma konumu: oynatılacak parça ve parçanın başından itibaren saniye.</summary>
public sealed record PlaybackPosition(RecordingSegment Segment, double OffsetSeconds)
{
    /// <summary>Konumun duvar saati karşılığı.</summary>
    public DateTimeOffset Time => Segment.StartedAt + TimeSpan.FromSeconds(OffsetSeconds);
}

/// <summary>
/// Kayıt parçalarından geri oynatma zaman çizelgesi üretir ve bir anın hangi parçaya düştüğünü bulur.
/// Saf fonksiyonlardır; veritabanına ve dosya sistemine dokunmaz.
/// </summary>
public static class PlaybackPlanner
{
    /// <summary>
    /// Aralarında bu kadar ya da daha az boşluk olan ardışık parçalar kesintisiz sayılır. ffmpeg parçaları anahtar
    /// karede böldüğünden bir parçanın bitişi ile sonrakinin başlangıcı arasında küçük farklar olabilir.
    /// </summary>
    public static readonly TimeSpan ContiguityTolerance = TimeSpan.FromSeconds(1.5);

    public static PlaybackTimeline BuildTimeline(
        IEnumerable<RecordingSegment> segments,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan? tolerance = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (to <= from)
        {
            throw new ArgumentException("Bitiş zamanı başlangıçtan sonra olmalı.", nameof(to));
        }

        var gapTolerance = tolerance ?? ContiguityTolerance;
        var ordered = Order(segments)
            .Where(s => s.EndedAt > from && s.StartedAt < to && s.EndedAt > s.StartedAt)
            .ToList();

        var items = ordered.Select(s => new TimelineItem(s.Id, s.StartedAt, s.EndedAt)).ToList();

        // Kesişen ya da tolerans içinde birbirine değen parçaları tek kayıtlı aralıkta birleştir.
        var merged = new List<TimeRange>();
        foreach (var segment in ordered)
        {
            if (merged.Count > 0 && segment.StartedAt - merged[^1].End <= gapTolerance)
            {
                var last = merged[^1];
                merged[^1] = last with { End = Max(last.End, segment.EndedAt) };
            }
            else
            {
                merged.Add(new TimeRange(segment.StartedAt, segment.EndedAt));
            }
        }

        var coverage = merged
            .Select(r => new TimeRange(Max(r.Start, from), Min(r.End, to)))
            .Where(r => r.End > r.Start)
            .ToList();

        var gaps = new List<TimeRange>();
        var cursor = from;
        foreach (var range in coverage)
        {
            if (range.Start > cursor)
            {
                gaps.Add(new TimeRange(cursor, range.Start));
            }

            cursor = Max(cursor, range.End);
        }

        if (cursor < to)
        {
            gaps.Add(new TimeRange(cursor, to));
        }

        return new PlaybackTimeline(from, to, items, gaps, coverage);
    }

    /// <summary>
    /// <paramref name="time"/> anını içeren parçayı ve parça içindeki konumu bulur. An bir boşluğa denk geliyorsa
    /// sonraki parçanın başını, sonrasında hiç kayıt yoksa <c>null</c> döner.
    /// </summary>
    public static PlaybackPosition? Locate(IEnumerable<RecordingSegment> segments, DateTimeOffset time)
    {
        ArgumentNullException.ThrowIfNull(segments);

        RecordingSegment? next = null;
        foreach (var segment in Order(segments))
        {
            if (segment.EndedAt <= segment.StartedAt)
            {
                continue;
            }

            if (segment.StartedAt <= time && time < segment.EndedAt)
            {
                return new PlaybackPosition(segment, (time - segment.StartedAt).TotalSeconds);
            }

            if (segment.StartedAt > time && next is null)
            {
                next = segment;
            }
        }

        return next is null ? null : new PlaybackPosition(next, 0);
    }

    private static IEnumerable<RecordingSegment> Order(IEnumerable<RecordingSegment> segments) =>
        segments.OrderBy(s => s.StartedAt).ThenBy(s => s.Id);

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
