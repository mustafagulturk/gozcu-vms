using System.Diagnostics;
using Gozcu.Vision;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gozcu.IntegrationTests.Vision;

/// <summary>
/// Testler arasında paylaşılan gerçek YOLOX tespit edicisi. Model yolu <c>GOZCU_MODEL_PATH</c> ortam değişkeninden,
/// yoksa depo kökündeki <c>models/yolox_tiny.onnx</c>'ten alınır. Model yoksa testler atlanmaz, açıklamayla başarısız olur.
/// </summary>
public sealed class DetectorFixture : IDisposable
{
    private readonly Lazy<OnnxYoloxDetector> _detector;

    public DetectorFixture()
    {
        ModelPath = ResolveModelPath();
        _detector = new Lazy<OnnxYoloxDetector>(() => new OnnxYoloxDetector(
            new YoloxDetectorOptions { ModelPath = ModelPath }, NullLogger.Instance));
    }

    public string ModelPath { get; }

    public OnnxYoloxDetector Detector
    {
        get
        {
            Assert.True(
                File.Exists(ModelPath),
                $"Model dosyası bulunamadı: {ModelPath}. Önce ./scripts/download-model.sh çalıştırın (veya GOZCU_MODEL_PATH ile model yolunu verin).");
            return _detector.Value;
        }
    }

    public void Dispose()
    {
        if (_detector.IsValueCreated)
        {
            _detector.Value.Dispose();
        }
    }

    /// <summary>
    /// Görüntüyü üretimdeki ffmpeg süzgeciyle model boyutuna letterbox eder (sol üste yerleşir, kalan alan 114 gri)
    /// ve ham BGR24 bayt dizisi olarak döndürür.
    /// </summary>
    public static async Task<byte[]> LetterboxAsync(string imagePath, int width, int height)
    {
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

        using var process = Process.Start(startInfo)!;
        using var frame = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(frame);
        var error = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(copy, error);
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"ffmpeg {imagePath} görüntüsünü letterbox edemedi: {await error}");
        Assert.Equal(width * height * 3, frame.Length);
        return frame.ToArray();
    }

    private static string ResolveModelPath()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("GOZCU_MODEL_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GozcuVms.sln")))
            {
                return Path.Combine(directory.FullName, "models", "yolox_tiny.onnx");
            }
        }

        throw new InvalidOperationException($"Depo kökü (GozcuVms.sln) {AppContext.BaseDirectory} üzerinde bulunamadı; GOZCU_MODEL_PATH ile model yolunu verin.");
    }
}
