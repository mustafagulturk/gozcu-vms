using System.Diagnostics;

namespace Gozcu.IntegrationTests.Infrastructure;

/// <summary>Ana makinedeki ffmpeg ile test için geçerli ve "çökmüş" MP4 dosyaları üretir.</summary>
public static class TestMedia
{
    private static readonly string[] CommonArguments = ["-hide_banner", "-loglevel", "error", "-nostdin", "-y"];
    private static readonly string[] TestSource = ["-f", "lavfi", "-i", "testsrc=size=320x240:rate=25"];

    /// <summary>Düzgün kapatılmış, moov atomu dosyanın sonunda olan (faststart olmayan) bir MP4.</summary>
    public static async Task WriteValidAsync(string path, int seconds = 3)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var process = Start([.. TestSource, "-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:v", "libx264", "-preset", "ultrafast", path]);
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"ffmpeg {path} dosyasını üretemedi: {await process.StandardError.ReadToEndAsync()}");
    }

    /// <summary>
    /// Kayıt sırasında elektrik kesilmesinin benzeri: ffmpeg gerçek zamanlı yazarken SIGKILL ile öldürülür;
    /// mdat verisi diskte kalır ama moov atomu hiç yazılmaz.
    /// </summary>
    public static async Task WriteKilledAsync(string path, long minimumBytes = 8 * 1024)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var process = Start(["-re", .. TestSource, "-t", "60", "-c:v", "libx264", "-preset", "ultrafast", path]);
        try
        {
            await Eventually.TrueAsync(
                () => File.Exists(path) && new FileInfo(path).Length >= minimumBytes, TimeSpan.FromSeconds(30), "ffmpeg dosyaya yazmaya başlamalı");
        }
        finally
        {
            // Process.Kill Unix'te SIGKILL gönderir; ffmpeg'in kapsayıcıyı kapatma fırsatı olmaz.
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    /// <summary>Geçerli bir dosyanın sonundaki moov atomunu kırpar (yarım kalan yazmanın diğer bir biçimi).</summary>
    public static async Task WriteTruncatedAsync(string path)
    {
        await WriteValidAsync(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        stream.SetLength(stream.Length * 2 / 3);
    }

    private static Process Start(IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in CommonArguments.Concat(arguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)!;
    }
}
