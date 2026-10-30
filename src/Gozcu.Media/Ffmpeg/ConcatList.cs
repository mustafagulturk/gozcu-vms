using System.Globalization;
using System.Text;

namespace Gozcu.Media.Ffmpeg;

/// <summary>ffmpeg concat demuxer listesindeki bir dosya.</summary>
/// <param name="FilePath">Parça dosyasının tam yolu.</param>
/// <param name="InPoint">Dosyanın başından itibaren okumanın başlayacağı nokta; yoksa dosyanın başı.</param>
/// <param name="OutPoint">Dosyanın başından itibaren okumanın biteceği nokta; yoksa dosyanın sonu.</param>
public sealed record ConcatEntry(string FilePath, TimeSpan? InPoint = null, TimeSpan? OutPoint = null);

/// <summary>
/// ffmpeg concat demuxer için liste dosyası ("ffconcat version 1.0") üretir.
/// Yollar tek tırnak içinde yazılır; yoldaki tek tırnak <c>'\''</c> biçiminde kaçırılır.
/// </summary>
public static class ConcatList
{
    public const string Header = "ffconcat version 1.0";

    public static string Build(IEnumerable<ConcatEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var builder = new StringBuilder().Append(Header).Append('\n');
        var count = 0;
        foreach (var entry in entries)
        {
            builder.Append("file ").Append(Quote(entry.FilePath)).Append('\n');
            if (entry.InPoint is { } inPoint)
            {
                builder.Append("inpoint ").Append(Seconds(inPoint)).Append('\n');
            }

            if (entry.OutPoint is { } outPoint)
            {
                builder.Append("outpoint ").Append(Seconds(outPoint)).Append('\n');
            }

            count++;
        }

        if (count == 0)
        {
            throw new ArgumentException("Birleştirme listesi en az bir dosya içermeli.", nameof(entries));
        }

        return builder.ToString();
    }

    /// <summary>Yolu concat listesi için tek tırnakla sarar: <c>a'b</c> → <c>'a'\''b'</c>.</summary>
    public static string Quote(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (path.Contains('\n', StringComparison.Ordinal) || path.Contains('\r', StringComparison.Ordinal) || path.Contains('\0', StringComparison.Ordinal))
        {
            // Satır sonu içeren bir yol liste dosyasına yeni yönerge enjekte edebilir.
            throw new ArgumentException("Dosya yolu satır sonu veya NUL karakteri içeremez.", nameof(path));
        }

        return "'" + path.Replace("'", @"'\''", StringComparison.Ordinal) + "'";
    }

    private static string Seconds(TimeSpan value) =>
        Math.Max(0, value.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);
}
