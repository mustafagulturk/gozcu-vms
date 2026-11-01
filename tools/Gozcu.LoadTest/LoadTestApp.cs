using System.Globalization;
using Gozcu.LoadTest.Cli;
using Gozcu.LoadTest.Engine;
using Gozcu.LoadTest.Reporting;
using Gozcu.LoadTest.Scenarios;

namespace Gozcu.LoadTest;

/// <summary>
/// Giriş noktası. Servis projeleri üst düzey deyimlerle global <c>Program</c> sınıfı ürettiği ve birim test projesi
/// hepsine başvurduğu için burada üst düzey deyim kullanılmaz; çakışmayı (CS0436) önlemek için benzersiz adlı sınıf.
/// </summary>
internal static class LoadTestApp
{
    public static async Task<int> Main(string[] args)
    {
        var parser = new ArgumentParser(ScenarioCatalog.Names, ScenarioCatalog.AllNames);
        var parsed = parser.Parse(args);
        if (parsed.ShowHelp)
        {
            Console.Write(HelpText.Usage());
            return 0;
        }

        if (parsed.ListScenarios)
        {
            Console.Write(HelpText.Scenarios());
            return 0;
        }

        if (parsed.Error is not null || parsed.Options is null)
        {
            await Console.Error.WriteLineAsync(parsed.Error);
            return 2;
        }

        var options = parsed.Options;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // İlk Ctrl+C testi durdurur ve o ana kadarki sonuçları yazar; ikincisi süreci sonlandırır.
            if (!cts.IsCancellationRequested)
            {
                e.Cancel = true;
                cts.Cancel();
            }
        };

        using var client = LoadEngine.CreateClient(options);
        var scenarios = options.Scenarios.Select(ScenarioCatalog.Get).ToList();
        var needs = scenarios.Aggregate(DataNeeds.None, (acc, s) => acc | s.Needs);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Gözcü yük testi: {string.Join(", ", options.Scenarios)} | {(options.OpenModel ? $"açık model {options.Rate} istek/sn" : "kapalı model")}, eşzamanlılık {options.Concurrency}, süre {DurationFormat.Format(options.Duration)}, ısınma {DurationFormat.Format(options.Warmup)}"));
        var data = await new TargetDiscovery(client, options.RecordingUrl, Console.Out).DiscoverAsync(needs, cts.Token);

        var engine = new LoadEngine(client, options);
        var runs = new List<RunResult>();
        foreach (var scenario in scenarios)
        {
            if (cts.IsCancellationRequested)
            {
                break;
            }

            if (scenario.CannotRunReason(data) is { } reason)
            {
                Console.WriteLine($"Atlandı: {scenario.Name} — {reason}.");
                Console.WriteLine();
                continue;
            }

            Console.WriteLine($"Çalışıyor: {scenario.Name} …");
            var result = await engine.RunAsync(scenario, data, cts.Token);
            runs.Add(result);
            Console.WriteLine(ReportFormatter.FormatConsole(result));
        }

        var report = new LoadTestReport(
            DateTimeOffset.UtcNow,
            Environment.MachineName,
            Environment.ProcessorCount,
            options.StreamingUrl.ToString(),
            options.RecordingUrl.ToString(),
            options.AnalysisUrl.ToString(),
            runs);

        if (options.JsonOutputPath is { } jsonPath)
        {
            await File.WriteAllTextAsync(jsonPath, ReportFormatter.FormatJson(report), CancellationToken.None);
            Console.WriteLine($"JSON yazıldı: {jsonPath}");
        }

        if (options.MarkdownOutputPath is { } mdPath)
        {
            await File.WriteAllTextAsync(mdPath, ReportFormatter.FormatMarkdown(report), CancellationToken.None);
            Console.WriteLine($"Markdown yazıldı: {mdPath}");
        }

        if (runs.Count == 0)
        {
            await Console.Error.WriteLineAsync("Hiçbir senaryo çalıştırılamadı.");
            return 1;
        }

        return 0;
    }
}
