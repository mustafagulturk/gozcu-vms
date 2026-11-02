using System.Diagnostics;

namespace Gozcu.Media.Processes;

/// <summary>Kısa süreli bir sürecin ikili stdout çıktısı, çıkış kodu ve son stderr satırları.</summary>
public sealed record ProcessOutput(int ExitCode, byte[] StandardOutput, IReadOnlyList<string> StandardErrorTail);

/// <summary>
/// Kısa süreli bir süreci çalıştırıp ikili stdout'unu belleğe okur (ör. ffmpeg'in pipe:1'e yazdığı tek JPEG kare).
/// <see cref="IProcessRunner"/> çıktıyı satır satır metin olarak verdiği için ikili veri için uygun değildir.
/// </summary>
public interface IProcessOutputReader
{
    /// <exception cref="ProcessOutputTooLargeException">stdout <paramref name="maxBytes"/>'ı aşarsa (süreç sonlandırılır).</exception>
    Task<ProcessOutput> ReadAllAsync(ProcessSpec spec, int maxBytes, CancellationToken cancellationToken);
}

public sealed class ProcessOutputReader : IProcessOutputReader
{
    private const int StandardErrorLines = 20;

    public async Task<ProcessOutput> ReadAllAsync(ProcessSpec spec, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        cancellationToken.ThrowIfCancellationRequested();

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

        var errors = new Queue<string>();
        using var process = new Process { StartInfo = startInfo };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (errors)
            {
                errors.Enqueue(e.Data);
                if (errors.Count > StandardErrorLines)
                {
                    errors.Dequeue();
                }
            }
        };

        process.Start();
        process.BeginErrorReadLine();

        try
        {
            // stdout okuması iptale her platformda tepki vermez; süreci öldürmek boruyu kapatır ve okuma 0 ile döner.
            await using (cancellationToken.Register(() => Kill(process)).ConfigureAwait(false))
            {
                using var output = new MemoryStream();
                var buffer = new byte[64 * 1024];
                var stdout = process.StandardOutput.BaseStream;
                int read;
                while ((read = await stdout.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) > 0)
                {
                    if (output.Length + read > maxBytes)
                    {
                        throw new ProcessOutputTooLargeException($"Süreç çıktısı {maxBytes} baytı aştı.");
                    }

                    output.Write(buffer, 0, read);
                }

                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                // Asenkron stderr okuyucusunun son satırları teslim etmesini bekle.
                process.WaitForExit();
                string[] tail;
                lock (errors)
                {
                    tail = [.. errors];
                }

                return new ProcessOutput(process.ExitCode, output.ToArray(), tail);
            }
        }
        catch
        {
            Kill(process);
            throw;
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
            // Süreç bu arada kendiliğinden çıktı.
        }
    }
}

public sealed class ProcessOutputTooLargeException : Exception
{
    public ProcessOutputTooLargeException(string message)
        : base(message)
    {
    }

    public ProcessOutputTooLargeException()
    {
    }

    public ProcessOutputTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
