using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Gozcu.LoadTest.Metrics;

/// <summary>
/// Tek bir uç noktanın (ya da toplamın) ölçümleri: gecikme histogramı, sonuç sayaçları ve alınan bayt.
/// Birden çok işçiden aynı anda çağrılabilir.
/// </summary>
internal sealed class EndpointRecorder
{
    private readonly ConcurrentDictionary<string, StrongBox<long>> _outcomes = new(StringComparer.Ordinal);
    private long _errors;
    private long _bytes;

    public EndpointRecorder(string name)
    {
        Name = name;
    }

    public string Name { get; }

    /// <summary>HTTP yanıtı alınan isteklerin gecikmesi (µs). Bağlantı hatası/zaman aşımı buraya girmez.</summary>
    public LatencyHistogram Latency { get; } = new();

    public long Errors => Interlocked.Read(ref _errors);

    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Toplam istek: yanıt alınanlar + taşıma hataları.</summary>
    public long Count => _outcomes.Values.Sum(b => Interlocked.Read(ref b.Value));

    /// <summary>Durum kodu ("200", "404") ya da hata türü ("zaman-aşımı") başına sayılar.</summary>
    public IReadOnlyDictionary<string, long> Outcomes =>
        _outcomes.ToDictionary(kv => kv.Key, kv => Interlocked.Read(ref kv.Value.Value), StringComparer.Ordinal);

    public void RecordResponse(int statusCode, long latencyMicroseconds, long bytes)
    {
        Latency.Record(latencyMicroseconds);
        Interlocked.Add(ref _bytes, bytes);
        Increment(OutcomeClassifier.StatusKey(statusCode), !OutcomeClassifier.IsSuccess(statusCode));
    }

    public void RecordFailure(string kind) => Increment(kind, isError: true);

    private void Increment(string key, bool isError)
    {
        var box = _outcomes.GetOrAdd(key, static _ => new StrongBox<long>());
        Interlocked.Increment(ref box.Value);
        if (isError)
        {
            Interlocked.Increment(ref _errors);
        }
    }
}

/// <summary>Bir yanıtın başarılı sayılıp sayılmadığı ve sayaç anahtarı.</summary>
internal static class OutcomeClassifier
{
    public const string Timeout = "zaman-aşımı";

    /// <summary>2xx ve 3xx başarılıdır (206 Partial Content dahil); 4xx/5xx hatadır.</summary>
    public static bool IsSuccess(int statusCode) => statusCode is >= 200 and < 400;

    public static string StatusKey(int statusCode) => statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Taşıma hataları için anahtar: "hata:ConnectionError" gibi.</summary>
    public static string TransportKey(HttpRequestException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return "hata:" + exception.HttpRequestError;
    }
}
