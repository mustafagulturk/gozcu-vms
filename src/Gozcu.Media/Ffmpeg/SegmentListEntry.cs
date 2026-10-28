using System.Globalization;

namespace Gozcu.Media.Ffmpeg;

/// <summary>ffmpeg segment muxer'ının CSV listesindeki bir satır: biten bir kayıt parçası.</summary>
/// <param name="FileName">Parça dosyasının adı (dizin olmadan).</param>
/// <param name="StartSeconds">Akış zamanına göre başlangıç.</param>
/// <param name="EndSeconds">Akış zamanına göre bitiş.</param>
public sealed record SegmentListEntry(string FileName, double StartSeconds, double EndSeconds)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(Math.Max(0, EndSeconds - StartSeconds));

    public static bool TryParse(string? line, out SegmentListEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        // Dosya adı virgül içerebilir ve ffmpeg bu durumda tırnak kullanır; sayılar her zaman sondadır.
        var lastComma = line.LastIndexOf(',');
        var middleComma = lastComma > 0 ? line.LastIndexOf(',', lastComma - 1) : -1;
        if (middleComma <= 0)
        {
            return false;
        }

        var name = line[..middleComma].Trim().Trim('"');
        if (name.Length == 0
            || !double.TryParse(line[(middleComma + 1)..lastComma], NumberStyles.Float, CultureInfo.InvariantCulture, out var start)
            || !double.TryParse(line[(lastComma + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var end))
        {
            return false;
        }

        entry = new SegmentListEntry(Path.GetFileName(name), start, end);
        return true;
    }
}
