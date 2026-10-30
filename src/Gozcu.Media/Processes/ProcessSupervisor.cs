using Microsoft.Extensions.Logging;

namespace Gozcu.Media.Processes;

/// <summary>
/// Bir süreci sürekli çalışır halde tutar: süreç çıktığında <see cref="ReconnectPolicy"/>
/// kadar bekleyip yeniden başlatır. Akış ve kayıt servislerinin RTSP bağlantı döngüsü budur.
/// </summary>
public sealed class ProcessSupervisor
{
    private readonly IProcessRunner _runner;
    private readonly ReconnectPolicy _policy;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    public ProcessSupervisor(IProcessRunner runner, ReconnectPolicy policy, TimeProvider timeProvider, ILogger logger)
    {
        _runner = runner;
        _policy = policy;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task RunAsync(Func<ProcessSpec> specFactory, ISupervisedProcessHandler handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(specFactory);
        ArgumentNullException.ThrowIfNull(handler);

        var failedAttempts = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var spec = specFactory();
            var startedAt = _timeProvider.GetTimestamp();
            int? exitCode = null;
            Exception? error = null;

            handler.OnStarted();
            try
            {
                exitCode = await _runner.RunAsync(spec, handler.OnStandardOutput, handler.OnStandardError, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                handler.OnStopped();
                return;
            }
            catch (Exception ex)
            {
                error = ex;
            }

            var uptime = _timeProvider.GetElapsedTime(startedAt);
            failedAttempts = uptime >= _policy.StableAfter ? 1 : failedAttempts + 1;
            var delay = _policy.GetDelay(failedAttempts);

            handler.OnExited(exitCode, error, delay);
            if (error is not null)
            {
                _logger.LogWarning(error, "Süreç başlatılamadı: {FileName}. {Delay} sonra tekrar denenecek", spec.FileName, delay);
            }
            else
            {
                _logger.LogWarning(
                    "Süreç {Uptime} çalıştıktan sonra {ExitCode} koduyla çıktı. {Delay} sonra yeniden başlatılacak (deneme {Attempt})",
                    uptime, exitCode, delay, failedAttempts);
            }

            try
            {
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                handler.OnStopped();
                return;
            }
        }

        handler.OnStopped();
    }
}

/// <summary>Denetlenen sürecin yaşam döngüsü olaylarını alan arayüz.</summary>
public interface ISupervisedProcessHandler
{
    void OnStarted();

    void OnStandardOutput(string line);

    void OnStandardError(string line);

    /// <param name="exitCode">Süreç başlatılamadıysa null.</param>
    /// <param name="error">Başlatma hatası, varsa.</param>
    /// <param name="retryDelay">Bir sonraki denemeye kadar beklenecek süre.</param>
    void OnExited(int? exitCode, Exception? error, TimeSpan retryDelay);

    void OnStopped();
}
