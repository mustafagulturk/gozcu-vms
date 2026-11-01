using Gozcu.Contracts.Cameras;
using Gozcu.Infrastructure.Cameras;
using Gozcu.Media.Ffmpeg;
using Gozcu.Media.Processes;
using Gozcu.RecordingService.Indexing;
using Microsoft.Extensions.Options;

namespace Gozcu.RecordingService.Recording;

/// <summary>
/// Katalogdaki her etkin kamera için yeniden kodlamasız bir ffmpeg kayıt süreci çalıştırır ve koparsa yeniden başlatır.
/// Kamera silinince ya da devre dışı bırakılınca süreç nazikçe durdurulur (son parça kapatılır); kayıtlar diskte kalır.
/// </summary>
public sealed class RecorderService : CameraWorkerHost
{
    private readonly RecordingOptions _options;
    private readonly IProcessRunner _runner;
    private readonly SegmentQueue _queue;
    private readonly RecorderRegistry _registry;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ReconnectPolicy _policy;

    public RecorderService(
        ICameraCatalog catalog,
        IOptions<RecordingOptions> options,
        IProcessRunner runner,
        SegmentQueue queue,
        RecorderRegistry registry,
        TimeProvider time,
        ILoggerFactory loggerFactory)
        : base(catalog, loggerFactory.CreateLogger<RecorderService>())
    {
        _options = options.Value;
        _runner = runner;
        _queue = queue;
        _registry = registry;
        _time = time;
        _loggerFactory = loggerFactory;
        _policy = new ReconnectPolicy
        {
            InitialDelay = _options.ReconnectInitialDelay,
            MaxDelay = _options.ReconnectMaxDelay,
        };
    }

    protected override string WorkerName => "kaydı";

    /// <summary>
    /// Ad kayıt dosyalarını etkilemez; yalnızca ad değiştiğinde kaydı kesmemek için süreç yalnızca adres değişince
    /// yeniden başlatılır.
    /// </summary>
    protected override bool RequiresRestart(CameraDefinition running, CameraDefinition desired)
    {
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(desired);
        return !string.Equals(running.RtspUrl?.OriginalString, desired.RtspUrl?.OriginalString, StringComparison.Ordinal);
    }

    protected override async Task RunCameraAsync(CameraDefinition camera, CancellationToken cancellationToken)
    {
        var directory = _options.CameraDirectory(camera.Id);
        Directory.CreateDirectory(directory);

        var recorder = new CameraRecorder(camera.Id, directory, _options.SegmentDuration, _queue.Enqueue, _time);
        _registry.Add(camera.Id, recorder);
        try
        {
            var spec = new ProcessSpec(
                _options.FfmpegPath,
                FfmpegCommands.SegmentRecorder(camera.RtspUrl!, directory, camera.Id, _options.SegmentDuration, _options.SocketTimeout))
            {
                Environment = FfmpegCommands.UtcEnvironment,
                GracefulStopInput = "q",
                GracefulStopTimeout = TimeSpan.FromSeconds(10),
            };

            var supervisor = new ProcessSupervisor(_runner, _policy, _time, _loggerFactory.CreateLogger($"Gozcu.Recorder.{camera.Id}"));
            await supervisor.RunAsync(() => spec, recorder, cancellationToken);
        }
        finally
        {
            _registry.Remove(camera.Id, recorder);
        }
    }
}
