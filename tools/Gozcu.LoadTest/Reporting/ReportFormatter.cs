using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Gozcu.LoadTest.Cli;

namespace Gozcu.LoadTest.Reporting;

/// <summary>Sonuçları konsol tablosu, Markdown ve JSON olarak biçimlendirir. Saf işlevlerdir.</summary>
internal static class ReportFormatter
{
    public static readonly IReadOnlyList<string> Headers =
        ["Uç nokta", "İstek", "Hata", "İstek/sn", "Ort ms", "p50 ms", "p90 ms", "p95 ms", "p99 ms", "Maks ms", "MiB"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // Türkçe karakterler \uXXXX yerine olduğu gibi yazılsın.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Tablo satırı: uç noktalar ve (birden fazlaysa) toplam.</summary>
    public static List<IReadOnlyList<string>> Rows(RunResult run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var rows = run.Endpoints.Select(Row).ToList();
        if (run.Endpoints.Count > 1)
        {
            rows.Add(Row(run.Total));
        }

        return rows;
    }

    public static IReadOnlyList<string> Row(EndpointSummary e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return
        [
            e.Name,
            N(e.Count),
            N(e.Errors),
            F(e.RequestsPerSecond, 1),
            F(e.MeanMs, 2),
            F(e.P50Ms, 2),
            F(e.P90Ms, 2),
            F(e.P95Ms, 2),
            F(e.P99Ms, 2),
            F(e.MaxMs, 2),
            F(e.Bytes / (1024.0 * 1024.0), 1),
        ];
    }

    /// <summary>Kutu çizgisiz, hizalı düz metin tablo. İlk sütun sola, diğerleri sağa yaslanır.</summary>
    public static string FormatTable(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);
        var widths = headers.Select((h, i) => Math.Max(h.Length, rows.Select(r => i < r.Count ? r[i].Length : 0).DefaultIfEmpty(0).Max())).ToArray();
        var sb = new StringBuilder();
        AppendLine(sb, headers, widths);
        sb.AppendLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in rows)
        {
            AppendLine(sb, row, widths);
        }

        return sb.ToString();
    }

    /// <summary>Bir koşunun konsol çıktısı: başlık, tablo, hata dağılımı.</summary>
    public static string FormatConsole(RunResult run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var sb = new StringBuilder();
        sb.AppendLine(Title(run));
        sb.Append(FormatTable(Headers, Rows(run)));
        var errors = ErrorBreakdown(run);
        if (errors.Length > 0)
        {
            sb.Append("Hatalar: ").AppendLine(errors);
        }

        if (run.Model == "open")
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Düşürülen (uçuştaki sınır dolu olduğu için başlatılamayan): {run.Dropped}");
        }

        return sb.ToString();
    }

    public static string FormatMarkdown(LoadTestReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.AppendLine("# Gözcü yük testi sonuçları");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Zaman: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Makine: {Md(report.MachineName)} ({report.ProcessorCount} çekirdek)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Servisler: yayın {report.StreamingUrl}, kayıt {report.RecordingUrl}, analiz {report.AnalysisUrl}");
        foreach (var run in report.Runs)
        {
            sb.AppendLine();
            sb.Append("## ").AppendLine(Md(Title(run)));
            sb.AppendLine();
            sb.Append("| ").Append(string.Join(" | ", ReportFormatter.Headers.Select(Md))).AppendLine(" |");
            sb.Append('|').Append(string.Join('|', ReportFormatter.Headers.Select((_, i) => i == 0 ? " --- " : " ---: "))).AppendLine("|");
            var rows = Rows(run);
            for (var r = 0; r < rows.Count; r++)
            {
                var isTotal = run.Endpoints.Count > 1 && r == rows.Count - 1;
                var cells = rows[r].Select((c, i) => isTotal && i == 0 ? $"**{Md(c)}**" : Md(c));
                sb.Append("| ").Append(string.Join(" | ", cells)).AppendLine(" |");
            }

            var errors = ErrorBreakdown(run);
            if (errors.Length > 0)
            {
                sb.AppendLine();
                sb.Append("Hatalar: ").AppendLine(Md(errors));
            }

            if (run.Model == "open")
            {
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"Düşürülen istek: {run.Dropped}");
            }
        }

        return sb.ToString();
    }

    public static string FormatJson(LoadTestReport report) => JsonSerializer.Serialize(report, JsonOptions);

    public static string Title(RunResult run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var model = run.Model == "open"
            ? string.Create(CultureInfo.InvariantCulture, $"açık model, {run.Rate:0.##} istek/sn, en fazla {run.Concurrency} uçuşta")
            : string.Create(CultureInfo.InvariantCulture, $"kapalı model, {run.Concurrency} işçi");
        return string.Create(CultureInfo.InvariantCulture,
            $"Senaryo: {run.Scenario} ({model}; ölçüm {DurationFormat.Format(TimeSpan.FromSeconds(Math.Round(run.MeasuredSeconds, 2)))}, ısınma {DurationFormat.Format(TimeSpan.FromSeconds(run.WarmupSeconds))})");
    }

    /// <summary>"timeline-1h 404×3, 500×1; segment-media hata:ConnectionError×2" biçiminde hata dağılımı.</summary>
    public static string ErrorBreakdown(RunResult run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var parts = run.Endpoints
            .Where(e => e.Errors > 0)
            .Select(e => e.Name + " " + string.Join(", ", e.Outcomes
                .Where(o => !IsSuccessKey(o.Key))
                .Select(o => string.Create(CultureInfo.InvariantCulture, $"{o.Key}×{o.Value}"))));
        return string.Join("; ", parts);
    }

    /// <summary>Markdown tablo hücresinde '|' ve satır sonu tabloyu bozar; kaçışla.</summary>
    public static string Md(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    }

    private static bool IsSuccessKey(string key) =>
        int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var status) && Metrics.OutcomeClassifier.IsSuccess(status);

    private static void AppendLine(StringBuilder sb, IReadOnlyList<string> cells, int[] widths)
    {
        for (var i = 0; i < widths.Length; i++)
        {
            var cell = i < cells.Count ? cells[i] : "";
            if (i > 0)
            {
                sb.Append("  ");
            }

            sb.Append(i == 0 ? cell.PadRight(widths[i]) : cell.PadLeft(widths[i]));
        }

        // Son sütundaki dolgu boşlukları yok.
        while (sb.Length > 0 && sb[^1] == ' ')
        {
            sb.Length--;
        }

        sb.AppendLine();
    }

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string F(double value, int decimals) => value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
