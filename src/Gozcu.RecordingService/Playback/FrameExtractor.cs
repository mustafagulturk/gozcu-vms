using Gozcu.Media.Ffmpeg;
using Gozcu.Media.Processes;
using Microsoft.Extensions.Options;

namespace Gozcu.RecordingService.Playback;

/// <summary>
/// Kayıt parçasından ffmpeg ile tek JPEG kare çıkarır. Her istek videoyu çözdüğü için (kayıt servisinde başka hiçbir
/// iş çözmez) aynı anda en fazla <see cref="MaxConcurrentExtractions"/> ffmpeg çalışır; fazlası sırada bekler.
/// </summary>
public sealed class FrameExtractor : IDisposable
{
    /// <summary>Aynı anda çalışabilecek ffmpeg sayısı; CPU'yu canlı kayda bırakmak için düşük tutulur.</summary>
    public const int MaxConcurrentExtractions = 2;

    /// <summary>Tek çıkarmanın süre sınırı; aşılırsa ffmpeg sonlandırılır.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Konum dosyanın sonuna düştüyse (boş çıktı) bu kadar geriden bir kez daha denenir.</summary>
    public static readonly TimeSpan RetryStepBack = TimeSpan.FromSeconds(1);

    private const int MaxJpegBytes = 8 * 1024 * 1024;

    private readonly IProcessOutputReader _reader;
    private readonly string _ffmpegPath;
    private readonly ILogger<FrameExtractor> _logger;
    private readonly SemaphoreSlim _slots = new(MaxConcurrentExtractions, MaxConcurrentExtractions);

    public FrameExtractor(IProcessOutputReader reader, IOptions<RecordingOptions> options, ILogger<FrameExtractor> logger)
        : this(reader, options.Value.FfmpegPath, logger)
    {
    }

    public FrameExtractor(IProcessOutputReader reader, string ffmpegPath, ILogger<FrameExtractor> logger)
    {
        _reader = reader;
        _ffmpegPath = ffmpegPath;
        _logger = logger;
    }

    /// <summary>
    /// <paramref name="path"/> dosyasının başından <paramref name="offset"/> sonraki kareyi JPEG olarak döner.
    /// Dosyada o konumda (ve biraz gerisinde) kare yoksa <c>null</c>.
    /// </summary>
    /// <exception cref="FrameExtractionException">ffmpeg hata verdiyse ya da süre sınırını aştıysa.</exception>
    public async Task<byte[]?> ExtractJpegAsync(string path, TimeSpan offset, int width, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (offset < TimeSpan.Zero)
        {
            offset = TimeSpan.Zero;
        }

        await _slots.WaitAsync(cancellationToken);
        try
        {
            var jpeg = await RunAsync(path, offset, width, cancellationToken);
            if (jpeg.Length == 0 && offset > TimeSpan.Zero)
            {
                // Veritabanındaki bitiş zamanı son karenin biraz ötesinde olabilir: biraz geriden bir kez daha dene.
                var retry = offset > RetryStepBack ? offset - RetryStepBack : TimeSpan.Zero;
                jpeg = await RunAsync(path, retry, width, cancellationToken);
            }

            return jpeg.Length == 0 ? null : jpeg;
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task<byte[]> RunAsync(string path, TimeSpan offset, int width, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var spec = new ProcessSpec(_ffmpegPath, FfmpegCommands.JpegFrame(path, offset, width));

        ProcessOutput output;
        try
        {
            output = await _reader.ReadAllAsync(spec, MaxJpegBytes, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Kare çıkarma {Timeout} içinde bitmedi: {Path}", Timeout, path);
            throw new FrameExtractionException($"ffmpeg {Timeout.TotalSeconds:0} saniyede yanıt vermedi.");
        }
        catch (ProcessOutputTooLargeException ex)
        {
            throw new FrameExtractionException(ex.Message, ex);
        }

        if (output.ExitCode != 0)
        {
            var detail = string.Join('\n', output.StandardErrorTail);
            _logger.LogWarning("Kare çıkarılamadı (ffmpeg çıkış kodu {ExitCode}): {Error}", output.ExitCode, detail);
            throw new FrameExtractionException($"ffmpeg {output.ExitCode} koduyla çıktı.");
        }

        return output.StandardOutput;
    }

    public void Dispose() => _slots.Dispose();
}

public sealed class FrameExtractionException : Exception
{
    public FrameExtractionException(string message)
        : base(message)
    {
    }

    public FrameExtractionException()
    {
    }

    public FrameExtractionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
