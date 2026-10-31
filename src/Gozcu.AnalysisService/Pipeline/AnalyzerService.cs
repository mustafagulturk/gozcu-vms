using System.Collections.Concurrent;
using Gozcu.Contracts.Cameras;
using Gozcu.Media.Ffmpeg;
using Gozcu.Media.Frames;
using Gozcu.Media.Processes;
using Microsoft.Extensions.Options;

namespace Gozcu.AnalysisService.Pipeline;

public sealed class AnalyzerRegistry
{
    private readonly ConcurrentDictionary<string, CameraAnalyzer> _analyzers = new(StringComparer.OrdinalIgnoreCase);

    public void Add(CameraAnalyzer analyzer) => _analyzers[analyzer.CameraId] = analyzer;

    public CameraAnalyzer? Find(string cameraId) => _analyzers.GetValueOrDefault(cameraId);

    public IEnumerable<CameraAnalysisStatus> Statuses() =>
        _analyzers.Values.Select(a => a.Status()).OrderBy(s => s.CameraId, StringComparer.Ordinal);
}

/// <summary>Etkin her kamera için bir <see cref="CameraAnalyzer"/> çalıştırır.</summary>
public sealed class AnalyzerService : BackgroundService
{
    private readonly IOptions<CameraOptions> _cameras;
    private readonly AnalysisOptions _options;
    private readonly DetectorProvider _detectors;
    private readonly IFrameSource _source;
    private readonly InferenceGate _gate;
    private readonly DetectionQueue _queue;
    private readonly AnalyzerRegistry _registry;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;

    public AnalyzerService(
        IOptions<CameraOptions> cameras,
        IOptions<AnalysisOptions> options,
        DetectorProvider detectors,
        IFrameSource source,
        InferenceGate gate,
        DetectionQueue queue,
        AnalyzerRegistry registry,
        TimeProvider time,
        ILoggerFactory loggerFactory)
    {
        _cameras = cameras;
        _options = options.Value;
        _detectors = detectors;
        _source = source;
        _gate = gate;
        _queue = queue;
        _registry = registry;
        _time = time;
        _loggerFactory = loggerFactory;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var logger = _loggerFactory.CreateLogger<AnalyzerService>();
        if (!_detectors.TryGet(out var detector))
        {
            logger.LogError(_detectors.Error, "Nesne tespit modeli yüklenemedi ({ModelPath}); analiz başlatılmadı", _options.ModelPath);
            return Task.CompletedTask;
        }

        var model = detector.Model;
        logger.LogInformation(
            "Model: {Model} ({Width}x{Height}), kamera başına {Fps} fps, en fazla {Concurrency} eşzamanlı çıkarım",
            model.Name, model.InputWidth, model.InputHeight, _options.FramesPerSecond, _gate.MaxConcurrency);

        var policy = new ReconnectPolicy
        {
            InitialDelay = _options.ReconnectInitialDelay,
            MaxDelay = _options.ReconnectMaxDelay,
        };

        var tasks = new List<Task>();
        foreach (var camera in _cameras.Value.Enabled)
        {
            var spec = new ProcessSpec(
                _options.FfmpegPath,
                FfmpegCommands.AnalysisFrames(camera.RtspUrl!, _options.FramesPerSecond, model.InputWidth, model.InputHeight, _options.SocketTimeout));
            var settings = new CameraAnalyzerSettings(
                camera.Id, camera.Name, new FrameSourceRequest(spec, model.InputWidth, model.InputHeight), policy, _options.Events);
            var analyzer = new CameraAnalyzer(
                settings, _source, detector, _gate, _queue, _time, _loggerFactory.CreateLogger($"Gozcu.Analyzer.{camera.Id}"));
            _registry.Add(analyzer);
            logger.LogInformation("{CameraId} analizi başlıyor: {Url}", camera.Id, camera.RtspUrl);
            tasks.Add(Task.Run(() => analyzer.RunAsync(stoppingToken), stoppingToken));
        }

        return Task.WhenAll(tasks);
    }
}
