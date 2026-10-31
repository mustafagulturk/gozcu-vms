using System.Threading.Channels;
using Gozcu.AnalysisService.Events;
using Gozcu.Contracts.Analysis;
using Gozcu.Media.Ffmpeg;
using Gozcu.Media.Frames;
using Gozcu.Media.Processes;
using Gozcu.Vision;

namespace Gozcu.AnalysisService.Pipeline;

public enum AnalyzerState
{
    /// <summary>Kaynak başlatıldı, ilk kare bekleniyor.</summary>
    Connecting,

    /// <summary>Kareler geliyor ve analiz ediliyor.</summary>
    Analyzing,

    /// <summary>Kaynak bitti ya da koptu; yeniden bağlanma bekleniyor.</summary>
    Reconnecting,

    Stopped,
}

/// <summary>Bir kamera analizcisinin ayarları.</summary>
/// <param name="Source">Kare kaynağı süreci; kare boyutu modelin giriş boyutudur.</param>
public sealed record CameraAnalyzerSettings(
    string CameraId,
    string Name,
    FrameSourceRequest Source,
    ReconnectPolicy Reconnect,
    DetectionEventOptions Events);

/// <summary>Son analiz edilen kare ve sonucu (anlık görüntü ve "son tespitler" API'si için).</summary>
public sealed record LatestAnalysis(byte[] Frame, AnalyzedFrame Result);

/// <summary>
/// Bir kameranın analiz hattı: kare kaynağını çalıştırır (koparsa <see cref="ReconnectPolicy"/> ile yeniden bağlanır),
/// kareleri tek elemanlı bir kanala koyar ve ayrı bir döngüde çıkarım yapar.
/// </summary>
/// <remarks>
/// Geri basınç: okuyucu ile çıkarım arasında kapasitesi 1 olan, "en eskiyi at" (DropOldest) kipinde bir kanal vardır.
/// Çıkarım yavaşsa bekleyen kare yenisiyle değiştirilir; böylece çıkarım her zaman en taze kare üzerinde çalışır ve
/// gecikme birikmez. Atılan kareler sayılır. ffmpeg'in stdout'u da hep okunduğu için boru dolup akışı durdurmaz.
/// Bellek: her kare için yeni bir dizi ayrılır (416×416×3 ≈ 0,5 MB; 2 fps × 4 kamera ≈ 4 MB/sn kısa ömürlü çöp).
/// Bu hızda havuzlamanın (ArrayPool) getireceği karmaşıklığa değmez; son kare anlık görüntü için ayrıca tutulur.
/// </remarks>
public sealed class CameraAnalyzer
{
    private const double AverageWeight = 0.2;
    private static readonly TimeSpan FpsWindow = TimeSpan.FromSeconds(10);

    private readonly CameraAnalyzerSettings _settings;
    private readonly IFrameSource _source;
    private readonly IObjectDetector _detector;
    private readonly InferenceGate _gate;
    private readonly DetectionQueue _output;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly DetectionEventTracker _tracker;
    private readonly LetterboxGeometry _modelOnlyGeometry;

    private readonly object _lock = new();
    private readonly Queue<DateTimeOffset> _recentAnalyses = new();
    private AnalyzerState _state = AnalyzerState.Connecting;
    private DateTimeOffset _stateSince;
    private StreamInfo? _streamInfo;
    private LetterboxGeometry? _geometry;
    private long _framesReceived;
    private long _framesDropped;
    private long _framesAnalyzed;
    private long _inferenceErrors;
    private double? _lastInferenceMs;
    private double? _averageInferenceMs;
    private LatestAnalysis? _latest;
    private IReadOnlyList<DetectionEventState> _openEvents = [];
    private int _runs;
    private string? _lastError;
    private DateTimeOffset? _nextRetryAt;

    public CameraAnalyzer(
        CameraAnalyzerSettings settings,
        IFrameSource source,
        IObjectDetector detector,
        InferenceGate gate,
        DetectionQueue output,
        TimeProvider time,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(detector);
        _settings = settings;
        _source = source;
        _detector = detector;
        _gate = gate;
        _output = output;
        _time = time;
        _logger = logger;
        _tracker = new DetectionEventTracker(settings.CameraId, settings.Events);
        _modelOnlyGeometry = new LetterboxGeometry(
            settings.Source.FrameWidth, settings.Source.FrameHeight, settings.Source.FrameWidth, settings.Source.FrameHeight);
        _stateSince = time.GetUtcNow();
    }

    public string CameraId => _settings.CameraId;

    public LatestAnalysis? Latest
    {
        get
        {
            lock (_lock)
            {
                return _latest;
            }
        }
    }

    /// <summary>İptal edilene kadar analiz eder; kaynak bittiğinde ya da koptuğunda yeniden bağlanır.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var policy = _settings.Reconnect;
        var failedAttempts = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var startedAt = _time.GetTimestamp();
                int? exitCode = null;
                Exception? error = null;
                OnRunStarting();

                var frames = Channel.CreateBounded<CapturedFrame>(
                    new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true },
                    _ => Interlocked.Increment(ref _framesDropped));
                var inference = Task.Run(() => InferenceLoopAsync(frames.Reader, cancellationToken), CancellationToken.None);
                try
                {
                    exitCode = await _source.RunAsync(_settings.Source, new SourceHandler(this, frames.Writer), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    // Kanalda kalan son kare (iptal edilmediyse) yine analiz edilir.
                    frames.Writer.TryComplete();
                    await inference;
                }

                // Akış bitti ya da koptu: açık olaylar son görüldükleri anla kapanır. Bağlantı geri gelince yeni olay açılır.
                Publish(null, _tracker.CloseAll());
                UpdateOpenEvents();
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // ProcessSupervisor ile aynı anlam: kararlı çalıştıysa deneme sayacı sıfırlanır.
                var uptime = _time.GetElapsedTime(startedAt);
                failedAttempts = uptime >= policy.StableAfter ? 1 : failedAttempts + 1;
                var delay = policy.GetDelay(failedAttempts);
                OnRunEnded(exitCode, error, delay);
                if (error is not null)
                {
                    _logger.LogWarning(error, "{CameraId} kare kaynağı başlatılamadı. {Delay} sonra tekrar denenecek", CameraId, delay);
                }
                else
                {
                    _logger.LogWarning(
                        "{CameraId} kare kaynağı {Uptime} çalıştıktan sonra {ExitCode} koduyla çıktı. {Delay} sonra yeniden bağlanılacak (deneme {Attempt})",
                        CameraId, uptime, exitCode, delay, failedAttempts);
                }

                try
                {
                    await Task.Delay(delay, _time, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            SetState(AnalyzerState.Stopped);
        }
    }

    public CameraAnalysisStatus Status()
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            PruneRecent(now);
            var fps = 0.0;
            if (_recentAnalyses.Count >= 2)
            {
                var span = (_recentAnalyses.Last() - _recentAnalyses.Peek()).TotalSeconds;
                fps = span > 0 ? (_recentAnalyses.Count - 1) / span : 0;
            }

            return new CameraAnalysisStatus(
                CameraId,
                string.IsNullOrWhiteSpace(_settings.Name) ? CameraId : _settings.Name,
                _state,
                _stateSince,
                _streamInfo,
                Interlocked.Read(ref _framesReceived),
                _framesAnalyzed,
                Interlocked.Read(ref _framesDropped),
                _inferenceErrors,
                Round(_lastInferenceMs),
                Round(_averageInferenceMs),
                Math.Round(fps, 2),
                _latest?.Result.CapturedAt,
                _latest?.Result.Detections.Count ?? 0,
                _openEvents.Select(e => new OpenEventStatus(e.Id, e.Category, e.StartedAt, e.ObjectCount, e.PeakConfidence)).ToList(),
                Math.Max(0, _runs - 1),
                _lastError,
                _nextRetryAt);
        }
    }

    private async Task InferenceLoopAsync(ChannelReader<CapturedFrame> frames, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in frames.ReadAllAsync(cancellationToken))
            {
                await AnalyzeAsync(frame, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task AnalyzeAsync(CapturedFrame frame, CancellationToken cancellationToken)
    {
        IReadOnlyList<Detection> detections;
        double inferenceMs;
        try
        {
            (detections, inferenceMs) = await _gate.RunAsync(
                () =>
                {
                    var started = _time.GetTimestamp();
                    var result = _detector.Detect(frame.Data, frame.Geometry);
                    return (result, _time.GetElapsedTime(started).TotalMilliseconds);
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _inferenceErrors++;
                _lastError = $"Çıkarım hatası: {ex.Message}";
            }

            _logger.LogWarning(ex, "{CameraId} karesi analiz edilemedi", CameraId);
            return;
        }

        var transitions = _tracker.Process(frame.CapturedAt, detections);
        var result = new AnalyzedFrame(CameraId, frame.CapturedAt, inferenceMs, frame.Geometry, detections);
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            _framesAnalyzed++;
            _lastInferenceMs = inferenceMs;
            _averageInferenceMs = _averageInferenceMs is { } average
                ? average + (AverageWeight * (inferenceMs - average))
                : inferenceMs;
            _recentAnalyses.Enqueue(now);
            PruneRecent(now);
            _latest = new LatestAnalysis(frame.Data, result);
        }

        UpdateOpenEvents();
        Publish(detections.Count > 0 ? result : null, transitions);
    }

    private void Publish(AnalyzedFrame? frame, IReadOnlyList<DetectionEventTransition> transitions)
    {
        foreach (var transition in transitions)
        {
            if (transition.Change == DetectionEventChange.Started)
            {
                _logger.LogInformation(
                    "{CameraId}: {Category} olayı başladı ({Count} nesne, %{Confidence:0})",
                    CameraId, transition.Event.Category, transition.Event.ObjectCount, transition.Event.Confidence * 100);
            }
            else if (transition.Change == DetectionEventChange.Ended)
            {
                _logger.LogInformation(
                    "{CameraId}: {Category} olayı bitti ({Seconds:0.0} sn, en fazla {MaxCount} nesne, {Frames} kare)",
                    CameraId, transition.Event.Category, (transition.Event.LastSeenAt - transition.Event.StartedAt).TotalSeconds,
                    transition.Event.MaxCount, transition.Event.FrameCount);
            }
        }

        if (frame is null && transitions.Count == 0)
        {
            return;
        }

        if (!_output.TryEnqueue(new AnalysisOutput(frame, transitions)))
        {
            _logger.LogWarning("{CameraId}: yazma kuyruğu dolu, analiz sonucu atıldı", CameraId);
        }
    }

    private void UpdateOpenEvents()
    {
        var open = _tracker.OpenEvents;
        lock (_lock)
        {
            _openEvents = open;
        }
    }

    private void OnRunStarting()
    {
        lock (_lock)
        {
            _runs++;
            _nextRetryAt = null;
            _streamInfo = null;
            _geometry = null;
            SetStateLocked(AnalyzerState.Connecting);
        }
    }

    private void OnRunEnded(int? exitCode, Exception? error, TimeSpan retryDelay)
    {
        lock (_lock)
        {
            if (error is not null)
            {
                _lastError = error.Message;
            }

            _lastError ??= $"ffmpeg {exitCode} koduyla çıktı";

            _nextRetryAt = _time.GetUtcNow() + retryDelay;
            SetStateLocked(AnalyzerState.Reconnecting);
        }
    }

    private void OnStreamInfo(StreamInfo info)
    {
        lock (_lock)
        {
            _streamInfo = info;
            _geometry = new LetterboxGeometry(_settings.Source.FrameWidth, _settings.Source.FrameHeight, info.Width, info.Height);
        }

        _logger.LogInformation("{CameraId} akışı: {Codec} {Width}x{Height}", CameraId, info.Codec, info.Width, info.Height);
    }

    private void OnErrorLine(string line)
    {
        lock (_lock)
        {
            _lastError = line;
        }
    }

    private void OnFrame(byte[] data, ChannelWriter<CapturedFrame> frames)
    {
        // Yakalama zamanı: kare borudan tamamen okunduğu an (bkz. CapturedFrame.CapturedAt gecikme notu).
        var capturedAt = _time.GetUtcNow();
        LetterboxGeometry geometry;
        lock (_lock)
        {
            if (_state != AnalyzerState.Analyzing)
            {
                _lastError = null;
                SetStateLocked(AnalyzerState.Analyzing);
            }

            // Kaynak boyutu bilinmiyorsa kare modelin kendisi kabul edilir (kutular yine 0..1 aralığında kalır).
            geometry = _geometry ?? _modelOnlyGeometry;
        }

        Interlocked.Increment(ref _framesReceived);

        // DropOldest: kanal doluysa bekleyen eski kare atılır (sayaç kanal geri çağrısında artar); yazma hep başarılıdır.
        frames.TryWrite(new CapturedFrame(data, capturedAt, geometry));
    }

    private void SetState(AnalyzerState state)
    {
        lock (_lock)
        {
            SetStateLocked(state);
        }
    }

    private void SetStateLocked(AnalyzerState state)
    {
        if (_state != state)
        {
            _state = state;
            _stateSince = _time.GetUtcNow();
        }
    }

    private void PruneRecent(DateTimeOffset now)
    {
        while (_recentAnalyses.Count > 0 && now - _recentAnalyses.Peek() > FpsWindow)
        {
            _recentAnalyses.Dequeue();
        }
    }

    private static double? Round(double? value) => value is null ? null : Math.Round(value.Value, 1);

    /// <summary>Tek bir kaynak çalıştırmasının olaylarını o çalıştırmanın kanalına bağlar.</summary>
    private sealed class SourceHandler(CameraAnalyzer analyzer, ChannelWriter<CapturedFrame> frames) : IFrameSourceHandler
    {
        public void OnStreamInfo(StreamInfo info) => analyzer.OnStreamInfo(info);

        public void OnErrorLine(string line) => analyzer.OnErrorLine(line);

        public void OnFrame(byte[] frame) => analyzer.OnFrame(frame, frames);
    }
}

public sealed record OpenEventStatus(Guid Id, ObjectCategory Category, DateTimeOffset StartedAt, int ObjectCount, float PeakConfidence);

/// <summary>Bir kameranın analiz durumu ve ölçümleri.</summary>
/// <param name="FramesReceived">ffmpeg'den okunan kare sayısı.</param>
/// <param name="FramesDropped">Çıkarım yetişemediği için atılan (yerine daha yenisi analiz edilen) kare sayısı.</param>
/// <param name="AnalysisFps">Son 10 saniyedeki etkin analiz hızı.</param>
/// <param name="LastFrameDetections">Son analiz edilen karedeki tespit sayısı.</param>
/// <param name="ReconnectCount">Kaynağın yeniden başlatılma sayısı.</param>
public sealed record CameraAnalysisStatus(
    string CameraId,
    string Name,
    AnalyzerState State,
    DateTimeOffset StateSince,
    StreamInfo? Source,
    long FramesReceived,
    long FramesAnalyzed,
    long FramesDropped,
    long InferenceErrors,
    double? LastInferenceMs,
    double? AverageInferenceMs,
    double AnalysisFps,
    DateTimeOffset? LastFrameAt,
    int LastFrameDetections,
    IReadOnlyList<OpenEventStatus> OpenEvents,
    int ReconnectCount,
    string? LastError,
    DateTimeOffset? NextRetryAt);
