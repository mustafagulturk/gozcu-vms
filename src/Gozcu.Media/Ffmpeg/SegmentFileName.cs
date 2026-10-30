using System.Globalization;
using System.Text.RegularExpressions;

namespace Gozcu.Media.Ffmpeg;

/// <summary>
/// Kayıt parçası dosya adı biçimi: "{kamera}_{yyyyMMdd}T{HHmmss}Z.mp4" (UTC başlangıç zamanı).
/// Dosya adının kendisi zaman bilgisini taşıdığından veritabanı olmadan da sıralama ve saklama yapılabilir.
/// </summary>
public static partial class SegmentFileName
{
    public const string Extension = ".mp4";

    private const string TimeFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>ffmpeg -strftime için desen.</summary>
    public static string Pattern(string cameraId) => $"{cameraId}_%Y%m%dT%H%M%SZ{Extension}";

    public static string Format(string cameraId, DateTimeOffset startUtc) =>
        $"{cameraId}_{startUtc.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture)}{Extension}";

    public static bool TryParse(string fileName, out string cameraId, out DateTimeOffset startUtc)
    {
        cameraId = string.Empty;
        startUtc = default;

        var match = NamePattern().Match(Path.GetFileName(fileName));
        if (!match.Success)
        {
            return false;
        }

        if (!DateTime.TryParseExact(
                match.Groups["time"].Value,
                TimeFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return false;
        }

        cameraId = match.Groups["camera"].Value;
        startUtc = new DateTimeOffset(parsed, TimeSpan.Zero);
        return true;
    }

    [GeneratedRegex(@"^(?<camera>[A-Za-z0-9_-]+)_(?<time>\d{8}T\d{6}Z)\.mp4$")]
    private static partial Regex NamePattern();
}
