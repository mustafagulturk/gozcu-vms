using System.Threading.Channels;
using Gozcu.Contracts.Cameras;
using Gozcu.Contracts.Events;
using Gozcu.Infrastructure.Cameras;
using Gozcu.Infrastructure.Messaging;
using Gozcu.Media.Ffmpeg;
using Gozcu.Media.Processes;
using Microsoft.Extensions.Options;

namespace Gozcu.StreamingService.Health;

/// <summary>
/// Katalogdaki her etkin kamera için bir ffmpeg izleme süreci çalıştırır, bağlantı koptuğunda yeniden bağlanır
/// ve sağlık durumu değişikliklerini RabbitMQ'ya yayınlar. Kamera eklenince izleme başlar, silinince ya da devre
/// dışı bırakılınca durur.
/// </summary>
public sealed class CameraMonitorService : CameraWorkerHost
{
    private static readonly TimeSpan EvaluateInterval = TimeSpan.FromSeconds(1);

    private readonly CameraRegistry _registry;
    private readonly StreamingOptions _options;
    private readonly IProcessRunner _runner;
    private readonly IEventPublisher _publisher;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CameraMonitorService> _logger;
    private readonly Channel<CameraStateTransition> _transitions = Channel.CreateUnbounded<CameraStateTransition>();
    private readonly ReconnectPolicy _policy;

    public CameraMonitorService(
        ICameraCatalog catalog,
        CameraRegistry registry,
        IOptions<StreamingOptions> options,
        IProcessRunner runner,
        IEventPublisher publisher,
        TimeProvider time,
        ILoggerFactory loggerFactory)
        : base(catalog, loggerFactory.CreateLogger<CameraMonitorService>())
    {
        _registry = registry;
        _options = options.Value;
        _runner = runner;
        _publisher = publisher;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CameraMonitorService>();
        _policy = new ReconnectPolicy
        {
            InitialDelay = _options.ReconnectInitialDelay,
            MaxDelay = _options.ReconnectMaxDelay,
        };
    }

    protected override string WorkerName => "izlemesi";

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(base.ExecuteAsync(stoppingToken), PublishTransitionsAsync(stoppingToken), EvaluateLoopAsync(stoppingToken));

    protected override async Task RunCameraAsync(CameraDefinition camera, CancellationToken cancellationToken)
    {
        var tracker = _registry.Add(camera);
        try
        {
            var monitor = new CameraMonitor(tracker, t => _transitions.Writer.TryWrite(t));
            var supervisor = new ProcessSupervisor(_runner, _policy, _time, _loggerFactory.CreateLogger($"Gozcu.Camera.{camera.Id}"));
            var spec = new ProcessSpec(_options.FfmpegPath, FfmpegCommands.Monitor(camera.RtspUrl!, _options.SocketTimeout))
            {
                GracefulStopInput = "q",
            };
            await supervisor.RunAsync(() => spec, monitor, cancellationToken);
        }
        finally
        {
            _registry.Remove(tracker);
        }
    }

    private async Task EvaluateLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(EvaluateInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var tracker in _registry.All)
                {
                    if (tracker.Evaluate() is { } transition)
                    {
                        _transitions.Writer.TryWrite(transition);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PublishTransitionsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var transition in _transitions.Reader.ReadAllAsync(stoppingToken))
            {
                _logger.LogInformation(
                    "{CameraId}: {Previous} → {Current} ({Reason})",
                    transition.CameraId, transition.Previous, transition.Current, transition.Reason ?? "-");
                try
                {
                    await _publisher.PublishAsync(
                        new CameraStatusChanged(transition.CameraId, transition.Previous, transition.Current, transition.Reason),
                        stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Kamera durum olayı yayınlanamadı: {Message}", ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
