using System.Text.Json;
using Gozcu.LoadTest.Metrics;
using Gozcu.LoadTest.Reporting;

namespace Gozcu.UnitTests.LoadTest;

public class ReportFormatterTests
{
    private static EndpointSummary Summary(string name, long count, long errors, Dictionary<string, long> outcomes) =>
        new(name, count, errors, count / 10.0, 12.345, 10, 20, 25, 40.5, 99.999, 2 * 1024 * 1024, outcomes);

    private static RunResult Run(string model = "closed", params EndpointSummary[] endpoints) =>
        new("mixed", model, 16, model == "open" ? 200 : null, 5, 10, model == "open" ? 7 : 0,
            new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero),
            endpoints,
            Summary("TOPLAM", endpoints.Sum(e => e.Count), endpoints.Sum(e => e.Errors), new() { ["200"] = 1 }));

    private static RunResult TwoEndpointRun(string model = "closed") => Run(
        model,
        Summary("cameras", 100, 0, new() { ["200"] = 100 }),
        Summary("timeline|1h", 50, 3, new() { ["200"] = 47, ["404"] = 2, [OutcomeClassifier.Timeout] = 1 }));

    [Fact]
    public void Table_aligns_columns()
    {
        var text = ReportFormatter.FormatTable(["Ad", "Sayı"], [["a", "1"], ["uzun-ad", "12345"]]);
        var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(
            [
                "Ad        Sayı",
                "-------  -----",
                "a            1",
                "uzun-ad  12345",
            ],
            lines);
    }

    [Fact]
    public void Rows_include_total_only_for_multiple_endpoints()
    {
        Assert.Equal(3, ReportFormatter.Rows(TwoEndpointRun()).Count);
        Assert.Single(ReportFormatter.Rows(Run("closed", Summary("cameras", 1, 0, new() { ["200"] = 1 }))));
    }

    [Fact]
    public void Row_formats_numbers_invariantly()
    {
        var row = ReportFormatter.Row(Summary("x", 100, 2, []));

        Assert.Equal(["x", "100", "2", "10.0", "12.35", "10.00", "20.00", "25.00", "40.50", "100.00", "2.0"], row);
    }

    [Fact]
    public void Error_breakdown_lists_only_failures()
    {
        var text = ReportFormatter.ErrorBreakdown(TwoEndpointRun());

        Assert.Equal("timeline|1h 404×2, zaman-aşımı×1", text);
    }

    [Fact]
    public void Markdown_has_header_alignment_row_and_escaped_cells()
    {
        var report = new LoadTestReport(DateTimeOffset.UnixEpoch, "makine", 8, "http://a/", "http://b/", "http://c/", [TwoEndpointRun()]);

        var md = ReportFormatter.FormatMarkdown(report);
        var lines = md.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        Assert.Contains("| Uç nokta | İstek | Hata | İstek/sn | Ort ms | p50 ms | p90 ms | p95 ms | p99 ms | Maks ms | MiB |", lines);
        Assert.Contains("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |", lines);
        Assert.Contains(lines, l => l.StartsWith("| timeline\\|1h | 50 | 3 |", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("| **TOPLAM** | 150 | 3 |", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("## Senaryo: mixed (kapalı model, 16 işçi;", StringComparison.Ordinal));
        // Her tablo satırında aynı sayıda (kaçışsız) ayraç olmalı.
        var tableLines = lines.Where(l => l.StartsWith('|')).ToList();
        Assert.All(tableLines, l => Assert.Equal(12, l.Replace("\\|", "", StringComparison.Ordinal).Count(c => c == '|')));
    }

    [Fact]
    public void Open_model_reports_dropped_requests()
    {
        var run = TwoEndpointRun("open");

        Assert.Contains("açık model, 200 istek/sn, en fazla 16 uçuşta", ReportFormatter.Title(run), StringComparison.Ordinal);
        Assert.Contains("Düşürülen", ReportFormatter.FormatConsole(run), StringComparison.Ordinal);
        Assert.DoesNotContain("Düşürülen", ReportFormatter.FormatConsole(TwoEndpointRun()), StringComparison.Ordinal);
    }

    [Fact]
    public void Json_is_camel_case_and_round_trips_numbers()
    {
        var report = new LoadTestReport(DateTimeOffset.UnixEpoch, "makine", 8, "http://a/", "http://b/", "http://c/", [TwoEndpointRun()]);

        using var doc = JsonDocument.Parse(ReportFormatter.FormatJson(report));
        var run = doc.RootElement.GetProperty("runs")[0];

        Assert.Equal("mixed", run.GetProperty("scenario").GetString());
        Assert.Equal(40.5, run.GetProperty("endpoints")[1].GetProperty("p99Ms").GetDouble());
        Assert.Equal(2, run.GetProperty("endpoints")[1].GetProperty("outcomes").GetProperty("404").GetInt64());
        Assert.Equal(150, run.GetProperty("total").GetProperty("count").GetInt64());
    }

    [Fact]
    public void Summary_from_recorder_computes_throughput_and_milliseconds()
    {
        var recorder = new EndpointRecorder("x");
        for (var i = 1; i <= 100; i++)
        {
            recorder.RecordResponse(200, i * 1_000, 1);
        }

        var s = EndpointSummary.From(recorder, measuredSeconds: 4);

        Assert.Equal(100, s.Count);
        Assert.Equal(25, s.RequestsPerSecond);
        Assert.Equal(50.5, s.MeanMs, 6);
        Assert.InRange(s.P50Ms, 50, 50 * (1 + (1.0 / 64)));
        Assert.Equal(100, s.MaxMs);
        Assert.Equal(100, s.Bytes);
    }
}
