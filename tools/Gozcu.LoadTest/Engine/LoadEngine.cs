using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using Gozcu.LoadTest.Cli;
using Gozcu.LoadTest.Metrics;
using Gozcu.LoadTest.Reporting;
using Gozcu.LoadTest.Scenarios;

namespace Gozcu.LoadTest.Engine;

/// <summary>
/// Yük motoru. İki model vardır:
/// <list type="bullet">
/// <item><b>Kapalı model</b> (varsayılan): N işçi döngüde istek atar; biri bitince sıradakini başlatır. Sistemin
/// N eşzamanlı kullanıcıyla ulaşabildiği en yüksek verimi ölçer.</item>
/// <item><b>Açık model</b> (<c>--rate</c>): istekler yanıtlardan bağımsız, sabit hızla gelir (gerçek kullanıcılar
/// sunucunun yavaşlamasını beklemez). Belirli bir yük altında gecikmeyi ölçer.</item>
/// </list>
/// <para>
/// <b>Koordineli ihmal (coordinated omission):</b> Kapalı modelde sunucu yavaşlarsa yük üretici de yavaşlar; 2 sn
/// takılan bir sunucu o sürede yalnızca N "yavaş" örnek üretir, oysa gerçek kullanıcılar o sürede yüzlerce istek
/// gönderip hepsi beklerdi. Sonuçta p99 gibi kuyruk yüzdelikleri olduğundan çok iyi görünür. Açık modelde bunu
/// önlemek için gecikme isteğin gerçekten gönderildiği andan değil, <i>planlandığı</i> andan ölçülür: istemci
/// geride kaldıysa bekleme süresi de gecikmeye eklenir. Uçuştaki istek sınırına (<c>--concurrency</c>) ulaşıldığı için
/// hiç başlatılamayan istekler sessizce atlanmaz, "düşürülen" olarak sayılır ve raporlanır; düşürülen sayısı sıfır
/// değilse sistem o hızı kaldıramıyor demektir ve yüzdelikler iyimserdir.
/// </para>
/// </summary>
internal sealed class LoadEngine
{
    private const int DrainBufferSize = 64 * 1024;
    private static readonly TimeSpan PacerTick = TimeSpan.FromMilliseconds(5);

    private readonly HttpClient _client;
    private readonly LoadTestOptions _options;

    public LoadEngine(HttpClient client, LoadTestOptions options)
    {
        _client = client;
        _options = options;
    }

    /// <summary>
    /// Tüm servislere tek, paylaşılan HttpClient. Sunucu başına bağlantı sayısı eşzamanlılıkla sınırlanır; bağlantılar
    /// DNS değişikliklerini görmek için belirli aralıklarla yenilenir.
    /// </summary>
    public static HttpClient CreateClient(LoadTestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = options.Concurrency,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            UseCookies = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = options.Timeout };
    }

    public async Task<RunResult> RunAsync(Scenario scenario, TargetData data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(data);

        var run = new RunState(scenario, data, _options);
        var startedAt = DateTimeOffset.UtcNow;

        if (_options.OpenModel)
        {
            await RunOpenAsync(run, cancellationToken);
        }
        else
        {
            await RunClosedAsync(run, cancellationToken);
        }

        var measuredTicks = Math.Max(0, Math.Min(Stopwatch.GetTimestamp(), run.MeasureEnd) - run.MeasureStart);
        if (!cancellationToken.IsCancellationRequested)
        {
            measuredTicks = run.MeasureEnd - run.MeasureStart;
        }

        var measuredSeconds = (double)measuredTicks / Stopwatch.Frequency;
        var endpoints = scenario.Labels
            .Select(l => run.Recorders.TryGetValue(l, out var r) ? r : new EndpointRecorder(l))
            .Concat(run.Recorders.Where(kv => !scenario.Labels.Contains(kv.Key)).Select(kv => kv.Value))
            .Select(r => EndpointSummary.From(r, measuredSeconds))
            .ToList();

        return new RunResult(
            scenario.Name,
            _options.OpenModel ? "open" : "closed",
            _options.Concurrency,
            _options.Rate,
            _options.Warmup.TotalSeconds,
            measuredSeconds,
            Interlocked.Read(ref run.Dropped),
            startedAt,
            endpoints,
            EndpointSummary.From(run.Total, measuredSeconds));
    }

    private async Task RunClosedAsync(RunState run, CancellationToken cancellationToken)
    {
        var workers = new Task[_options.Concurrency];
        for (var w = 0; w < workers.Length; w++)
        {
            workers[w] = Task.Run(
                async () =>
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var now = Stopwatch.GetTimestamp();
                        if (now >= run.MeasureEnd)
                        {
                            return;
                        }

                        await ExecuteAsync(run, now, cancellationToken);
                    }
                },
                CancellationToken.None);
        }

        await Task.WhenAll(workers);
    }

    private async Task RunOpenAsync(RunState run, CancellationToken cancellationToken)
    {
        var rate = _options.Rate!.Value;
        var limit = _options.Concurrency;
        using var inFlight = new SemaphoreSlim(limit, limit);
        using var timer = new PeriodicTimer(PacerTick);
        long issued = 0;
        var done = false;

        try
        {
            while (!done)
            {
                var now = Stopwatch.GetTimestamp();
                var scheduled = OpenModelPacer.ScheduledCount(now - run.Start, Stopwatch.Frequency, rate);
                for (; issued < scheduled; issued++)
                {
                    var intended = OpenModelPacer.IntendedTimestamp(run.Start, issued, Stopwatch.Frequency, rate);
                    if (intended >= run.MeasureEnd)
                    {
                        done = true;
                        break;
                    }

                    if (!inFlight.Wait(0, CancellationToken.None))
                    {
                        // Sınıra ulaşıldı: istek başlatılamadı. Sessizce atlamak koordineli ihmal olurdu.
                        if (intended >= run.MeasureStart)
                        {
                            Interlocked.Increment(ref run.Dropped);
                        }

                        continue;
                    }

                    _ = ExecuteAndReleaseAsync(run, intended, inFlight, cancellationToken);
                }

                if (!done && !await timer.WaitForNextTickAsync(cancellationToken))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ctrl+C: o ana kadarki sonuçlar raporlanır.
        }

        // Uçuştaki isteklerin bitmesini bekle: tüm yuvalar geri alınabildiğinde hiçbiri kalmamıştır.
        for (var i = 0; i < limit; i++)
        {
            await inFlight.WaitAsync(CancellationToken.None);
        }
    }

    private async Task ExecuteAndReleaseAsync(RunState run, long intendedTimestamp, SemaphoreSlim inFlight, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield();
            await ExecuteAsync(run, intendedTimestamp, cancellationToken);
        }
        finally
        {
            inFlight.Release();
        }
    }

    /// <summary>
    /// Tek istek: yanıt gövdesi sonuna kadar okunur (son bayta kadar süre). Gecikme <paramref name="startTimestamp"/>'tan
    /// ölçülür; açık modelde bu planlanan zamandır. Isınma süresinde başlayan istekler kaydedilmez.
    /// </summary>
    private async Task ExecuteAsync(RunState run, long startTimestamp, CancellationToken cancellationToken)
    {
        var spec = run.Scenario.Next(new ScenarioInput(run.Data, Random.Shared, DateTimeOffset.UtcNow));
        var record = startTimestamp >= run.MeasureStart;
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUrl(spec.Target), spec.PathAndQuery));
        if (spec.RangeStart is { } rangeStart)
        {
            request.Headers.Range = new RangeHeaderValue(rangeStart, spec.RangeEnd);
        }

        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var bytes = await DrainAsync(response.Content, cancellationToken);
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            if (record)
            {
                var micros = elapsed.Ticks / (TimeSpan.TicksPerMillisecond / 1000);
                run.Recorder(spec.Label).RecordResponse((int)response.StatusCode, micros, bytes);
                run.Total.RecordResponse((int)response.StatusCode, micros, bytes);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Test durduruldu; yarım kalan istek sayılmaz.
        }
        catch (OperationCanceledException)
        {
            // HttpClient.Timeout aşıldı.
            RecordFailure(run, spec.Label, record, OutcomeClassifier.Timeout);
        }
        catch (HttpRequestException ex)
        {
            RecordFailure(run, spec.Label, record, OutcomeClassifier.TransportKey(ex));
        }
        catch (IOException)
        {
            RecordFailure(run, spec.Label, record, "hata:IO");
        }
    }

    private static void RecordFailure(RunState run, string label, bool record, string kind)
    {
        if (record)
        {
            run.Recorder(label).RecordFailure(kind);
            run.Total.RecordFailure(kind);
        }
    }

    private static async Task<long> DrainAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(DrainBufferSize);
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                total += read;
            }

            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private Uri BaseUrl(ServiceTarget target) => target switch
    {
        ServiceTarget.Streaming => _options.StreamingUrl,
        ServiceTarget.Recording => _options.RecordingUrl,
        ServiceTarget.Analysis => _options.AnalysisUrl,
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    /// <summary>Bir koşunun paylaşılan durumu: zaman sınırları, kaydediciler ve düşürülen sayacı.</summary>
    private sealed class RunState
    {
        public long Dropped;

        public RunState(Scenario scenario, TargetData data, LoadTestOptions options)
        {
            Scenario = scenario;
            Data = data;
            Start = Stopwatch.GetTimestamp();
            MeasureStart = Start + (long)(options.Warmup.TotalSeconds * Stopwatch.Frequency);
            MeasureEnd = MeasureStart + (long)(options.Duration.TotalSeconds * Stopwatch.Frequency);
            foreach (var label in scenario.Labels)
            {
                Recorders[label] = new EndpointRecorder(label);
            }
        }

        public Scenario Scenario { get; }

        public TargetData Data { get; }

        public long Start { get; }

        public long MeasureStart { get; }

        public long MeasureEnd { get; }

        public ConcurrentDictionary<string, EndpointRecorder> Recorders { get; } = new(StringComparer.Ordinal);

        public EndpointRecorder Total { get; } = new("TOPLAM");

        public EndpointRecorder Recorder(string label) => Recorders.GetOrAdd(label, static l => new EndpointRecorder(l));
    }
}
