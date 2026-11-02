using System.ComponentModel;
using System.Diagnostics;

namespace Gozcu.Benchmarks.Support;

/// <summary>
/// Ölçümler için model boyutunda BGR24 kare üretir. ffmpeg ve test görüntüsü varsa gerçek bir otopark karesi
/// (üretimdeki letterbox süzgeciyle) kullanılır; böylece çıkarım ve kod çözme gerçekçi sayıda aday kutu görür.
/// Aksi hâlde sabit tohumlu rastgele baytlar kullanılır (sonuçlar yine tekrarlanabilir).
/// </summary>
internal static class BenchmarkFrames
{
    /// <summary>Entegrasyon testlerinin kullandığı 768x432 otopark karesi.</summary>
    public const string TestImage = "tests/Gozcu.IntegrationTests/TestData/parking-person-bicycle-car.jpg";

    public const int TestImageWidth = 768;
    public const int TestImageHeight = 432;

    /// <summary>Sabit tohumlu rastgele BGR24 kare.</summary>
    public static byte[] Synthetic(int width, int height, int seed = 42)
    {
        var frame = new byte[width * height * 3];
        new Random(seed).NextBytes(frame);
        return frame;
    }

    /// <summary>
    /// Test görüntüsünü ffmpeg ile <paramref name="width"/>×<paramref name="height"/> boyutuna letterbox eder.
    /// Görüntü ya da ffmpeg yoksa veya ffmpeg hata verirse <c>null</c> döner.
    /// </summary>
    public static byte[]? TryLoadTestImage(int width, int height)
    {
        var imagePath = RepoFiles.FindUpwards(TestImage);
        if (imagePath is null)
        {
            return null;
        }

        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        string[] arguments =
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-i", imagePath,
            "-vf", $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:0:0:color=0x727272",
            "-pix_fmt", "bgr24", "-f", "rawvideo", "pipe:1",
        ];
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            using var frame = new MemoryStream();
            var error = process.StandardError.ReadToEndAsync();
            process.StandardOutput.BaseStream.CopyTo(frame);
            process.WaitForExit();
            _ = error.GetAwaiter().GetResult();
            return process.ExitCode == 0 && frame.Length == width * height * 3 ? frame.ToArray() : null;
        }
        catch (Win32Exception)
        {
            // ffmpeg kurulu değil; sentetik kareye düşülür.
            return null;
        }
    }
}
