using Gozcu.Contracts.Cameras;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gozcu.Infrastructure.Cameras;

/// <summary>
/// Kamera başına bir iş (izleme, kayıt, analiz) çalıştıran servislerin ortak tabanı. Katalog her değiştiğinde
/// istenen kamera kümesiyle çalışan kümeyi uzlaştırır:
/// <list type="bullet">
/// <item>katalogda yeni olan ya da yeniden etkinleşen kamera için iş başlatılır,</item>
/// <item>silinen ya da devre dışı bırakılan kameranın işi durdurulur,</item>
/// <item>adı ya da RTSP adresi değişen kameranın işi durdurulup yeni tanımla başlatılır.</item>
/// </list>
/// Durdurma, işin iptal belirtecini iptal edip görevin bitmesini beklemektir; böylece örneğin kayıt süreci son
/// parçayı düzgün kapatmadan aynı kamera için ikinci bir süreç açılmaz. Beklenmedik biçimde biten iş bir süre sonra
/// yeniden başlatılır.
/// </summary>
public abstract class CameraWorkerHost : BackgroundService
{
    private readonly ICameraCatalog _catalog;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, Worker> _workers = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource _workerExited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected CameraWorkerHost(ICameraCatalog catalog, ILogger logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    /// <summary>Beklenmedik biçimde biten bir işin yeniden başlatılmadan önce bekleyeceği süre.</summary>
    protected virtual TimeSpan WorkerRestartDelay => TimeSpan.FromSeconds(5);

    /// <summary>Günlüklerde işin adı (ör. "izleme", "kayıt").</summary>
    protected abstract string WorkerName { get; }

    /// <summary>Şu an işi çalışan kameraların tanımları, kimliğe göre sıralı.</summary>
    public IReadOnlyList<CameraDefinition> RunningCameras
    {
        get
        {
            lock (_lock)
            {
                return _workers.Values.Select(w => w.Camera).OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
            }
        }
    }

    /// <summary>Kamera için iş çalışmalı mı? Varsayılan: etkin ve geçerli bir RTSP adresi var.</summary>
    protected virtual bool ShouldRun(CameraDefinition camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        return camera.Enabled && CameraValidation.IsValidRtspUrl(camera.RtspUrl);
    }

    /// <summary>Çalışan iş yeni tanımla yeniden başlatılmalı mı? Varsayılan: ad ya da adres değiştiyse.</summary>
    protected virtual bool RequiresRestart(CameraDefinition running, CameraDefinition desired)
    {
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(desired);
        return !string.Equals(running.Name, desired.Name, StringComparison.Ordinal)
            || !string.Equals(running.RtspUrl?.OriginalString, desired.RtspUrl?.OriginalString, StringComparison.Ordinal);
    }

    /// <summary>
    /// Bir kameranın işi. <paramref name="cancellationToken"/> iptal edilene kadar çalışmalı ve iptalde temizlik yapıp
    /// (ör. kayıt tablosundan çıkıp) dönmelidir.
    /// </summary>
    protected abstract Task RunCameraAsync(CameraDefinition camera, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _catalog.WaitUntilLoadedAsync(stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                TaskCompletionSource exited;
                lock (_lock)
                {
                    exited = _workerExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                var version = _catalog.Version;
                await ReconcileAsync(_catalog.Cameras, stoppingToken).ConfigureAwait(false);

                var changed = _catalog.WaitForChangeAsync(version, stoppingToken);
                await Task.WhenAny(changed, exited.Task).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            List<Worker> all;
            lock (_lock)
            {
                all = [.. _workers.Values];
            }

            await StopWorkersAsync(all).ConfigureAwait(false);
        }
    }

    /// <summary>İstenen kümeyle çalışan kümeyi uzlaştırır.</summary>
    internal async Task ReconcileAsync(IReadOnlyList<CameraDefinition> cameras, CancellationToken stoppingToken)
    {
        var desired = cameras.Where(ShouldRun)
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        List<Worker> toStop;
        lock (_lock)
        {
            toStop = _workers.Values
                .Where(w => w.Exited
                    || !desired.TryGetValue(w.Camera.Id, out var wanted)
                    || RequiresRestart(w.Camera, wanted))
                .ToList();
        }

        foreach (var worker in toStop)
        {
            var reason = worker.Exited ? "iş beklenmedik biçimde bitti"
                : desired.ContainsKey(worker.Camera.Id) ? "tanımı değişti" : "silindi ya da devre dışı";
            _logger.LogInformation("{CameraId} {Worker} durduruluyor ({Reason})", worker.Camera.Id, WorkerName, reason);
        }

        await StopWorkersAsync(toStop).ConfigureAwait(false);

        foreach (var camera in desired.Values.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            lock (_lock)
            {
                if (_workers.ContainsKey(camera.Id))
                {
                    continue;
                }
            }

            StartWorker(camera, stoppingToken);
        }
    }

    private void StartWorker(CameraDefinition camera, CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "{CameraId} {Worker} başlatılıyor: {Url}", camera.Id, WorkerName, RtspUrlMasking.MaskPassword(camera.RtspUrl));
        var worker = new Worker(camera, CancellationTokenSource.CreateLinkedTokenSource(stoppingToken));
        lock (_lock)
        {
            _workers[camera.Id] = worker;
        }

        worker.Task = Task.Run(() => RunWorkerAsync(worker), CancellationToken.None);
    }

    private async Task RunWorkerAsync(Worker worker)
    {
        var camera = worker.Camera;
        var cancellationToken = worker.Cancellation.Token;
        try
        {
            await RunCameraAsync(camera, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{CameraId} {Worker} hatayla sonlandı", camera.Id, WorkerName);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _logger.LogWarning("{CameraId} {Worker} beklenmedik biçimde bitti; {Delay} sonra yeniden başlatılacak", camera.Id, WorkerName, WorkerRestartDelay);
        try
        {
            await Task.Delay(WorkerRestartDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Uzlaştırmayı uyandır: biten iş durdurulmuş sayılır ve kamera hâlâ isteniyorsa yeniden başlatılır.
        TaskCompletionSource exited;
        lock (_lock)
        {
            worker.Exited = true;
            exited = _workerExited;
        }

        exited.TrySetResult();
    }

    private async Task StopWorkersAsync(IReadOnlyCollection<Worker> workers)
    {
        if (workers.Count == 0)
        {
            return;
        }

        foreach (var worker in workers)
        {
            await worker.Cancellation.CancelAsync().ConfigureAwait(false);
        }

        await Task.WhenAll(workers.Select(w => w.Task)).ConfigureAwait(false);
        lock (_lock)
        {
            foreach (var worker in workers)
            {
                if (_workers.TryGetValue(worker.Camera.Id, out var current) && ReferenceEquals(current, worker))
                {
                    _workers.Remove(worker.Camera.Id);
                }

                worker.Cancellation.Dispose();
            }
        }
    }

    private sealed class Worker(CameraDefinition camera, CancellationTokenSource cancellation)
    {
        public CameraDefinition Camera { get; } = camera;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task Task { get; set; } = Task.CompletedTask;

        /// <summary>İş iptal edilmeden kendiliğinden bitti (yalnızca kilit altında okunur/yazılır).</summary>
        public bool Exited { get; set; }
    }
}
