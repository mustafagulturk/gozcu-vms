using System.Collections.Concurrent;
using Gozcu.Contracts.Cameras;
using Gozcu.Infrastructure.Cameras;
using Gozcu.Media.Ffmpeg;
using Gozcu.Media.Frames;
using Gozcu.Media.Processes;
using Gozcu.Vision;
using Microsoft.Extensions.Options;

namespace Gozcu.AnalysisService.Pipeline;

/// <summary>Çalışan kamera analizcileri. Kamera kataloğu değiştikçe eklenir ve çıkarılır.</summary>
public sealed class AnalyzerRegistry
{
    private readonly ConcurrentDictionary<string, CameraAnalyzer> _analyzers = new(StringComparer.OrdinalIgnoreCase);

    public void Add(CameraAnalyzer analyzer)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        _analyzers[analyzer.CameraId] = analyzer;
    }

    /// <summary>Analizciyi çıkarır; bu arada aynı kimlikle yenisi eklendiyse ona dokunmaz.</summary>
    public bool Remove(CameraAnalyzer analyzer)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        return _analyzers.TryRemove(KeyValuePair.Create(analyzer.CameraId, analyzer));
    }

    public CameraAnalyzer? Find(string cameraId) => _analyzers.GetValueOrDefault(cameraId);

    public IEnumerable<CameraAnalysisStatus> Statuses() =>
        _analyzers.Values.Select(a => a.Status()).OrderBy(s => s.CameraId, StringComparer.Ordinal);
}

/// <summary>
/// Katalogdaki her etkin kamera için bir <see cref="CameraAnalyzer"/> çalıştırır. Kamera silinince ya da devre dışı
/// bırakılınca analizci durdurulur; açık olayları kapanır.
/// </summary>
public sealed class AnalyzerService : CameraWorkerHost
{
    private readonly AnalysisOptions _options;
    private readonly DetectorProvider _detectors;
    private readonly IFrameSource _source;
    private readonly InferenceGate _gate;
    private readonly DetectionQueue _queue;
    private readonly AnalyzerRegistry _registry;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AnalyzerService> _logger;
    private readonly ReconnectPolicy _policy;
    private IObjectDetector? _detector;

    public AnalyzerService(
        ICameraCatalog catalog,
        IOptions<AnalysisOptions> options,
        DetectorProvider detectors,
        IFrameSource source,
        InferenceGate gate,
        DetectionQueue queue,
        AnalyzerRegistry registry,
        TimeProvider time,
        ILoggerFactory loggerFactory)
        : base(catalog, loggerFactory.CreateLogger<AnalyzerService>())
    {
        _options = options.Value;
        _detectors = detectors;
        _source = source;
        _gate = gate;
        _queue = queue;
        _registry = registry;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AnalyzerService>();
        _policy = new ReconnectPolicy
        {
            InitialDelay = _options.ReconnectInitialDelay,
            MaxDelay = _options.ReconnectMaxDelay,
        };
    }

    protected override string WorkerName => "analizi";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_detectors.TryGet(out var detector))
        {
            _logger.LogError(_detectors.Error, "Nesne tespit modeli yüklenemedi ({ModelPath}); analiz başlatılmadı", _options.ModelPath);
            return Task.CompletedTask;
        }

        _detector = detector;
        var model = detector.Model;
        _logger.LogInformation(
            "Model: {Model} ({Width}x{Height}), kamera başına {Fps} fps, en fazla {Concurrency} eşzamanlı çıkarım",
            model.Name, model.InputWidth, model.InputHeight, _options.FramesPerSecond, _gate.MaxConcurrency);
        return base.ExecuteAsync(stoppingToken);
    }

    protected override async Task RunCameraAsync(CameraDefinition camera, CancellationToken cancellationToken)
    {
        var detector = _detector!;
        var model = detector.Model;
        var spec = new ProcessSpec(
            _options.FfmpegPath,
            FfmpegCommands.AnalysisFrames(camera.RtspUrl!, _options.FramesPerSecond, model.InputWidth, model.InputHeight, _options.SocketTimeout));
        var settings = new CameraAnalyzerSettings(
            camera.Id, camera.Name, new FrameSourceRequest(spec, model.InputWidth, model.InputHeight), _policy, _options.Events);
        var analyzer = new CameraAnalyzer(
            settings, _source, detector, _gate, _queue, _time, _loggerFactory.CreateLogger($"Gozcu.Analyzer.{camera.Id}"));
        _registry.Add(analyzer);
        try
        {
            await analyzer.RunAsync(cancellationToken);
        }
        finally
        {
            _registry.Remove(analyzer);
        }
    }
}
