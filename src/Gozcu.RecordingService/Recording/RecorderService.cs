using Gozcu.Contracts.Cameras;
using Gozcu.Media.Ffmpeg;
using Gozcu.Media.Processes;
using Gozcu.RecordingService.Indexing;
using Microsoft.Extensions.Options;

namespace Gozcu.RecordingService.Recording;

/// <summary>Her kamera için yeniden kodlamasız bir ffmpeg kayıt süreci çalıştırır ve koparsa yeniden başlatır.</summary>
public sealed class RecorderService : BackgroundService
{
    private readonly IOptions<CameraOptions> _cameras;
    private readonly RecordingOptions _options;
    private readonly IProcessRunner _runner;
    private readonly SegmentQueue _queue;
    private readonly RecorderRegistry _registry;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;

    public RecorderService(
        IOptions<CameraOptions> cameras,
        IOptions<RecordingOptions> options,
        IProcessRunner runner,
        SegmentQueue queue,
        RecorderRegistry registry,
        TimeProvider time,
        ILoggerFactory loggerFactory)
    {
        _cameras = cameras;
        _options = options.Value;
        _runner = runner;
        _queue = queue;
        _registry = registry;
        _time = time;
        _loggerFactory = loggerFactory;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var policy = new ReconnectPolicy
        {
            InitialDelay = _options.ReconnectInitialDelay,
            MaxDelay = _options.ReconnectMaxDelay,
        };
        var logger = _loggerFactory.CreateLogger<RecorderService>();

        var tasks = new List<Task>();
        foreach (var camera in _cameras.Value.Enabled)
        {
            var directory = _options.CameraDirectory(camera.Id);
            Directory.CreateDirectory(directory);

            var recorder = new CameraRecorder(camera.Id, directory, _options.SegmentDuration, _queue.Enqueue, _time);
            _registry.Add(camera.Id, recorder);

            var spec = new ProcessSpec(
                _options.FfmpegPath,
                FfmpegCommands.SegmentRecorder(camera.RtspUrl!, directory, camera.Id, _options.SegmentDuration, _options.SocketTimeout))
            {
                Environment = FfmpegCommands.UtcEnvironment,
                GracefulStopInput = "q",
                GracefulStopTimeout = TimeSpan.FromSeconds(10),
            };

            var supervisor = new ProcessSupervisor(_runner, policy, _time, _loggerFactory.CreateLogger($"Gozcu.Recorder.{camera.Id}"));
            logger.LogInformation("{CameraId} kaydı başlıyor → {Directory} ({Seconds} sn parçalar)", camera.Id, directory, _options.SegmentSeconds);
            tasks.Add(Task.Run(() => supervisor.RunAsync(() => spec, recorder, stoppingToken), stoppingToken));
        }

        return Task.WhenAll(tasks);
    }
}
