using Gozcu.Media.Ffmpeg;
using Gozcu.Media.Processes;
using Microsoft.Extensions.Options;

namespace Gozcu.RecordingService.Playback;

/// <summary>
/// Kayıt parçalarını ffmpeg concat demuxer ile yeniden kodlamadan tek bir MP4 klibe birleştirir.
/// Her dışa aktarma kendi geçici dizininde çalışır; dönen akış kapatıldığında dizin silinir.
/// </summary>
public sealed class ClipExporter : IDisposable
{
    /// <summary>Aynı anda çalışabilecek dışa aktarma sayısı; disk ve CPU'yu canlı kayda bırakmak için düşük tutulur.</summary>
    public const int MaxConcurrentExports = 2;

    private const int StandardErrorLines = 20;

    private readonly IProcessRunner _runner;
    private readonly string _ffmpegPath;
    private readonly ILogger<ClipExporter> _logger;
    private readonly SemaphoreSlim _slots = new(MaxConcurrentExports, MaxConcurrentExports);

    public ClipExporter(IProcessRunner runner, IOptions<RecordingOptions> options, ILogger<ClipExporter> logger)
        : this(runner, options.Value.FfmpegPath, logger)
    {
    }

    public ClipExporter(IProcessRunner runner, string ffmpegPath, ILogger<ClipExporter> logger)
    {
        _runner = runner;
        _ffmpegPath = ffmpegPath;
        _logger = logger;
    }

    /// <summary>
    /// Klibi üretir ve okunmak üzere açılmış akışını döner. Akış kapatıldığında MP4 ve geçici dizin silinir.
    /// </summary>
    /// <exception cref="ClipExportException">ffmpeg başarısız olduysa.</exception>
    public async Task<ExportedClipStream> ExportAsync(IReadOnlyList<ConcatEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            throw new ArgumentException("Dışa aktarılacak parça yok.", nameof(entries));
        }

        await _slots.WaitAsync(cancellationToken);
        var directory = Directory.CreateTempSubdirectory("gozcu-export-").FullName;
        try
        {
            var listPath = Path.Combine(directory, "list.txt");
            var outputPath = Path.Combine(directory, "clip.mp4");
            await File.WriteAllTextAsync(listPath, ConcatList.Build(entries), cancellationToken);

            var errors = new Queue<string>();
            var spec = new ProcessSpec(_ffmpegPath, ClipExportCommands.Concat(listPath, outputPath));
            var exitCode = await _runner.RunAsync(
                spec,
                _ => { },
                line =>
                {
                    lock (errors)
                    {
                        errors.Enqueue(line);
                        if (errors.Count > StandardErrorLines)
                        {
                            errors.Dequeue();
                        }
                    }
                },
                cancellationToken);

            File.Delete(listPath);
            if (exitCode != 0 || !File.Exists(outputPath))
            {
                string detail;
                lock (errors)
                {
                    detail = string.Join('\n', errors);
                }

                _logger.LogWarning("Klip dışa aktarılamadı (ffmpeg çıkış kodu {ExitCode}): {Error}", exitCode, detail);
                throw new ClipExportException($"ffmpeg {exitCode} koduyla çıktı.");
            }

            var stream = new ExportedClipStream(outputPath, directory);
            _logger.LogInformation("{Count} parçadan {Bytes} baytlık klip üretildi", entries.Count, stream.Length);
            return stream;
        }
        catch
        {
            ExportedClipStream.DeleteDirectory(directory);
            throw;
        }
        finally
        {
            _slots.Release();
        }
    }

    public void Dispose() => _slots.Dispose();
}

/// <summary>Dışa aktarılmış klibin okuma akışı; kapatıldığında klibin geçici dizinini siler.</summary>
public sealed class ExportedClipStream : FileStream
{
    private readonly string _directory;

    internal ExportedClipStream(string path, string directory)
        : base(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose)
    {
        _directory = directory;
    }

    /// <summary>Klibin geçici dizini (testler için).</summary>
    public string DirectoryPath => _directory;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        DeleteDirectory(_directory);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        DeleteDirectory(_directory);
    }

    internal static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Geçici dizin; işletim sisteminin temizliğine bırakılır.
        }
    }
}

public sealed class ClipExportException : Exception
{
    public ClipExportException(string message)
        : base(message)
    {
    }

    public ClipExportException()
    {
    }

    public ClipExportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
