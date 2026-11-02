using System.Globalization;

namespace Gozcu.LoadTest.Cli;

/// <summary>"500ms", "30s", "2m", "1h" ya da yalın saniye ("45") biçimindeki süreleri ayrıştırır.</summary>
internal static class DurationFormat
{
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim().ToLowerInvariant();
        (string number, double unitSeconds) = s switch
        {
            _ when s.EndsWith("ms", StringComparison.Ordinal) => (s[..^2], 0.001),
            _ when s.EndsWith('s') => (s[..^1], 1),
            _ when s.EndsWith('m') => (s[..^1], 60),
            _ when s.EndsWith('h') => (s[..^1], 3600),
            _ => (s, 1),
        };

        if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
            || double.IsNaN(amount) || double.IsInfinity(amount))
        {
            return false;
        }

        var seconds = amount * unitSeconds;
        if (seconds > TimeSpan.MaxValue.TotalSeconds / 2)
        {
            return false;
        }

        value = TimeSpan.FromSeconds(seconds);
        return true;
    }

    /// <summary>Kısa ve okunur biçim: 250ms, 1.5s, 2m, 90m, 1h.</summary>
    public static string Format(TimeSpan value)
    {
        if (value == TimeSpan.Zero)
        {
            return "0s";
        }

        if (value < TimeSpan.FromSeconds(1))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{value.TotalMilliseconds:0.##}ms");
        }

        if (value < TimeSpan.FromMinutes(1) || value.TotalSeconds % 60 != 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{value.TotalSeconds:0.##}s");
        }

        if (value < TimeSpan.FromHours(1) || value.TotalMinutes % 60 != 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{value.TotalMinutes:0}m");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value.TotalHours:0}h");
    }
}
