using System.Threading.Channels;
using Gozcu.Contracts.Cameras;
using Gozcu.Contracts.Events;
using Gozcu.Infrastructure.Messaging;
using Gozcu.Media.Ffmpeg;
using Gozcu.Media.Processes;
using Microsoft.Extensions.Options;

namespace Gozcu.StreamingService.Health;

/// <summary>
/// Her kamera için bir ffmpeg izleme süreci çalıştırır, bağlantı koptuğunda yeniden bağlanır
/// ve sağlık durumu değişikliklerini RabbitMQ'ya yayınlar.
/// </summary>
public sealed class CameraMonitorService : BackgroundService
{
    private static readonly TimeSpan EvaluateInterval = TimeSpan.FromSeconds(1);

    private readonly CameraRegistry _registry;
    private readonly IOptions<CameraOptions> _cameras;
    private readonly StreamingOptions _options;
    private readonly IProcessRunner _runner;
    private readonly IEventPublisher _publisher;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CameraMonitorService> _logger;
    private readonly Channel<CameraStateTransition> _transitions = Channel.CreateUnbounded<CameraStateTransition>();

    public CameraMonitorService(
        CameraRegistry registry,
        IOptions<CameraOptions> cameras,
        IOptions<StreamingOptions> options,
        IProcessRunner runner,
        IEventPublisher publisher,
        TimeProvider time,
        ILoggerFactory loggerFactory)
    {
        _registry = registry;
        _cameras = cameras;
        _options = options.Value;
        _runner = runner;
        _publisher = publisher;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CameraMonitorService>();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var policy = new ReconnectPolicy
        {
            InitialDelay = _options.ReconnectInitialDelay,
            MaxDelay = _options.ReconnectMaxDelay,
        };

        var tasks = new List<Task> { PublishTransitionsAsync(stoppingToken), EvaluateLoopAsync(stoppingToken) };
        foreach (var camera in _cameras.Value.Enabled)
        {
            var tracker = _registry.Find(camera.Id)!;
            var monitor = new CameraMonitor(tracker, t => _transitions.Writer.TryWrite(t));
            var supervisor = new ProcessSupervisor(_runner, policy, _time, _loggerFactory.CreateLogger($"Gozcu.Camera.{camera.Id}"));
            var spec = new ProcessSpec(_options.FfmpegPath, FfmpegCommands.Monitor(camera.RtspUrl!, _options.SocketTimeout))
            {
                GracefulStopInput = "q",
            };
            _logger.LogInformation("{CameraId} kamerası izlenmeye başlanıyor: {Url}", camera.Id, camera.RtspUrl);
            tasks.Add(Task.Run(() => supervisor.RunAsync(() => spec, monitor, stoppingToken), stoppingToken));
        }

        return Task.WhenAll(tasks);
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
