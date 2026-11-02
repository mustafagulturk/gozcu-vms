namespace Gozcu.LoadTest.Cli;

/// <summary>Komut satırından gelen yük testi ayarları.</summary>
internal sealed record LoadTestOptions
{
    public static readonly Uri DefaultStreamingUrl = new("http://localhost:5080");
    public static readonly Uri DefaultRecordingUrl = new("http://localhost:5081");
    public static readonly Uri DefaultAnalysisUrl = new("http://localhost:5082");

    /// <summary>Çalıştırılacak senaryolar, verildiği sırayla ("all" çözülmüş hâli).</summary>
    public IReadOnlyList<string> Scenarios { get; init; } = ["mixed"];

    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan Warmup { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Kapalı modelde işçi sayısı; açık modelde aynı anda uçuşta olabilecek en fazla istek.</summary>
    public int Concurrency { get; init; } = 16;

    /// <summary>Verilirse açık model: saniyede sabit sayıda istek başlatılır.</summary>
    public double? Rate { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public Uri StreamingUrl { get; init; } = DefaultStreamingUrl;

    public Uri RecordingUrl { get; init; } = DefaultRecordingUrl;

    public Uri AnalysisUrl { get; init; } = DefaultAnalysisUrl;

    public string? JsonOutputPath { get; init; }

    public string? MarkdownOutputPath { get; init; }

    public bool OpenModel => Rate is not null;
}
