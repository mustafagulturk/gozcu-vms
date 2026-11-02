using System.Diagnostics;
using Gozcu.Media.Ffmpeg;

namespace Gozcu.Media.Frames;

/// <summary>
/// ffmpeg'i başlatıp stdout'undan ham kareleri, stderr'inden giriş akışı bilgisini ve hata satırlarını okur.
/// İptalde süreç doğrudan sonlandırılır: çözücünün kapatması gereken bir dosya yoktur.
/// </summary>
public sealed class FfmpegFrameSource : IFrameSource
{
    /// <summary>
    /// İlk kare okunduğunda giriş akışı bilgisi henüz gelmediyse en fazla bu kadar beklenir. stdout ve stderr ayrı
    /// borulardır; ffmpeg bilgiyi kod çözmeden önce yazsa da satırın bize ulaşma sırası garanti değildir.
    /// </summary>
    private static readonly TimeSpan StreamInfoWait = TimeSpan.FromSeconds(2);

    public async Task<int> RunAsync(FrameSourceRequest request, IFrameSourceHandler handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(handler);
        cancellationToken.ThrowIfCancellationRequested();

        var spec = request.Process;
        var startInfo = new ProcessStartInfo(spec.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in spec.Environment)
        {
            startInfo.Environment[key] = value;
        }

        var streamInfoSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamInfoParsed = false;

        using var process = new Process { StartInfo = startInfo };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                streamInfoSeen.TrySetResult();
                return;
            }

            // Yalnızca ilk video satırı giriş akışıdır; sonraki "Video: rawvideo" satırı bizim çıkışımız.
            if (!streamInfoParsed && StreamInfo.TryParse(e.Data, out var info) && info is not null)
            {
                streamInfoParsed = true;
                handler.OnStreamInfo(info);
                streamInfoSeen.TrySetResult();
            }
            else if (LooksLikeError(e.Data))
            {
                handler.OnErrorLine(e.Data.Trim());
            }
        };

        process.Start();
        process.BeginErrorReadLine();

        // stdout okuması iptale her platformda tepki vermez; süreci öldürmek boruyu kapatır ve okuma 0 ile döner.
        await using (cancellationToken.Register(() => Kill(process)).ConfigureAwait(false))
        {
            var endedCleanly = false;
            try
            {
                var reader = new RawFrameReader(process.StandardOutput.BaseStream, request.FrameSize);
                var first = true;
                while (true)
                {
                    // Her kare için yeni dizi: kare tüketiciye devredilir (bkz. IFrameSourceHandler.OnFrame).
                    var frame = new byte[request.FrameSize];
                    if (!await reader.ReadAsync(frame, cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }

                    if (first)
                    {
                        first = false;
                        await WaitForStreamInfoAsync(streamInfoSeen.Task, cancellationToken).ConfigureAwait(false);
                    }

                    handler.OnFrame(frame);
                }

                endedCleanly = true;
            }
            catch (EndOfStreamException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Süreç bir karenin ortasında çıktı; eksik kare atılır, çıkış kodu aşağıda okunur.
                handler.OnErrorLine(ex.Message);
                endedCleanly = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Süreç aşağıda durdurulup beklendikten sonra iptal yeniden fırlatılır.
            }
            catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
            {
                handler.OnErrorLine(ex.Message);
            }
            catch (IOException)
            {
                // İptal sırasında boru kapandı.
            }
            finally
            {
                // stdout kapandıysa ffmpeg zaten çıkıyordur; aksi halde (iptal, hata) süreci durdur.
                if (!endedCleanly || cancellationToken.IsCancellationRequested)
                {
                    Kill(process);
                }
            }
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        // Asenkron stderr okuyucusunun son satırları teslim etmesini bekle.
        process.WaitForExit();
        cancellationToken.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    private static async Task WaitForStreamInfoAsync(Task streamInfoSeen, CancellationToken cancellationToken)
    {
        try
        {
            await streamInfoSeen.WaitAsync(StreamInfoWait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Bilgi satırı gelmedi; kareler kaynak boyutu bilinmeden işlenir.
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Süreç bu arada kendiliğinden çıktı ya da hiç başlamadı.
        }
    }

    internal static bool LooksLikeError(string line) =>
        line.Contains("error", StringComparison.OrdinalIgnoreCase)
        || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
        || line.Contains("timed out", StringComparison.OrdinalIgnoreCase)
        || line.Contains("refused", StringComparison.OrdinalIgnoreCase)
        || line.Contains("not found", StringComparison.OrdinalIgnoreCase)
        || line.Contains("invalid", StringComparison.OrdinalIgnoreCase);
}
