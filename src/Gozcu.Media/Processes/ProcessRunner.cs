using System.Diagnostics;

namespace Gozcu.Media.Processes;

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<int> RunAsync(
        ProcessSpec spec,
        Action<string> onStandardOutput,
        Action<string> onStandardError,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo(spec.FileName)
        {
            RedirectStandardInput = true,
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

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onStandardOutput(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onStandardError(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopAsync(process, spec).ConfigureAwait(false);
            throw;
        }

        // Asenkron okuyucuların son satırları teslim etmesini bekle.
        process.WaitForExit();
        return process.ExitCode;
    }

    private static async Task StopAsync(Process process, ProcessSpec spec)
    {
        if (process.HasExited)
        {
            return;
        }

        if (spec.GracefulStopInput is not null)
        {
            try
            {
                await process.StandardInput.WriteAsync(spec.GracefulStopInput).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(spec.GracefulStopTimeout);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                process.WaitForExit();
                return;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
            {
                // Nazik durdurma başarısız oldu; zorla sonlandırmaya geç.
            }
        }

        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        catch (InvalidOperationException)
        {
            // Süreç bu arada kendiliğinden çıktı.
        }
    }
}
