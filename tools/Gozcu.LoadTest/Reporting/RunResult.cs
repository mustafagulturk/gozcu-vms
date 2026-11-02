using Gozcu.LoadTest.Metrics;

namespace Gozcu.LoadTest.Reporting;

/// <summary>Bir uç noktanın (ya da toplamın) özet istatistikleri. Süreler milisaniyedir.</summary>
internal sealed record EndpointSummary(
    string Name,
    long Count,
    long Errors,
    double RequestsPerSecond,
    double MeanMs,
    double P50Ms,
    double P90Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs,
    long Bytes,
    IReadOnlyDictionary<string, long> Outcomes)
{
    public static EndpointSummary From(EndpointRecorder recorder, double measuredSeconds)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        var h = recorder.Latency;
        var count = recorder.Count;
        return new EndpointSummary(
            recorder.Name,
            count,
            recorder.Errors,
            measuredSeconds > 0 ? count / measuredSeconds : 0,
            h.Mean / 1000.0,
            h.Percentile(50) / 1000.0,
            h.Percentile(90) / 1000.0,
            h.Percentile(95) / 1000.0,
            h.Percentile(99) / 1000.0,
            h.Max / 1000.0,
            recorder.Bytes,
            new SortedDictionary<string, long>(recorder.Outcomes.ToDictionary(StringComparer.Ordinal), StringComparer.Ordinal));
    }
}

/// <summary>Bir senaryo koşusunun sonucu.</summary>
internal sealed record RunResult(
    string Scenario,
    string Model,
    int Concurrency,
    double? Rate,
    double WarmupSeconds,
    double MeasuredSeconds,
    long Dropped,
    DateTimeOffset StartedAt,
    IReadOnlyList<EndpointSummary> Endpoints,
    EndpointSummary Total);

/// <summary>JSON/Markdown çıktısının kökü.</summary>
internal sealed record LoadTestReport(
    DateTimeOffset GeneratedAt,
    string MachineName,
    int ProcessorCount,
    string StreamingUrl,
    string RecordingUrl,
    string AnalysisUrl,
    IReadOnlyList<RunResult> Runs);
