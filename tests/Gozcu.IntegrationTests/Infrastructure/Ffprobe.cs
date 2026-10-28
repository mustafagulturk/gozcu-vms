using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Gozcu.IntegrationTests.Infrastructure;

public sealed record ProbeResult(double DurationSeconds, IReadOnlyList<string> VideoCodecs);

public static class Ffprobe
{
    public static async Task<ProbeResult> ProbeAsync(string path)
    {
        var startInfo = new ProcessStartInfo("ffprobe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "-v", "error", "-show_entries", "format=duration:stream=codec_type,codec_name", "-of", "json", path })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe {path} okuyamadı: {error}");
        }

        using var json = JsonDocument.Parse(output);
        var duration = double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        var codecs = json.RootElement.GetProperty("streams").EnumerateArray()
            .Where(s => s.GetProperty("codec_type").GetString() == "video")
            .Select(s => s.GetProperty("codec_name").GetString()!)
            .ToList();
        return new ProbeResult(duration, codecs);
    }
}
