using System.Globalization;
using System.Text.RegularExpressions;

namespace Gozcu.Media.Ffmpeg;

/// <summary>ffmpeg'in giriş bilgisi satırından okunan video akışı özellikleri.</summary>
public sealed partial record StreamInfo(string Codec, int Width, int Height, double? FramesPerSecond)
{
    /// <summary>
    /// "Stream #0:0: Video: h264 (High), yuv420p(progressive), 1280x720 [SAR 1:1 DAR 16:9], 25 fps, ..." satırını ayrıştırır.
    /// </summary>
    public static bool TryParse(string line, out StreamInfo? info)
    {
        info = null;
        var match = VideoLine().Match(line);
        if (!match.Success)
        {
            return false;
        }

        double? fps = null;
        var fpsMatch = FpsPattern().Match(line);
        if (fpsMatch.Success && double.TryParse(fpsMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFps))
        {
            fps = parsedFps;
        }

        info = new StreamInfo(
            match.Groups["codec"].Value,
            int.Parse(match.Groups["w"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture),
            fps);
        return true;
    }

    [GeneratedRegex(@"Stream #\d+:\d+.*?: Video: (?<codec>\w+).*?, (?<w>\d{2,5})x(?<h>\d{2,5})")]
    private static partial Regex VideoLine();

    [GeneratedRegex(@", ([\d.]+) fps")]
    private static partial Regex FpsPattern();
}
