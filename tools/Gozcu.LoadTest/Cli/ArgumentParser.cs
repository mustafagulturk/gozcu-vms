using System.Globalization;

namespace Gozcu.LoadTest.Cli;

/// <summary>Ayrıştırma sonucu: ayarlar, yardım/liste isteği ya da hata iletisi.</summary>
internal sealed record ParseResult(LoadTestOptions? Options, bool ShowHelp, bool ListScenarios, string? Error)
{
    public static ParseResult Help() => new(null, true, false, null);

    public static ParseResult List() => new(null, false, true, null);

    public static ParseResult Fail(string error) => new(null, false, false, error);

    public static ParseResult Ok(LoadTestOptions options) => new(options, false, false, null);
}

/// <summary>
/// Elle yazılmış küçük komut satırı ayrıştırıcısı. Hem "--ad değer" hem "--ad=değer" biçimini kabul eder.
/// Bağımlılık eklememek için System.CommandLine kullanılmadı.
/// </summary>
internal sealed class ArgumentParser
{
    private readonly IReadOnlyCollection<string> _knownScenarios;
    private readonly IReadOnlyList<string> _allScenarios;

    /// <param name="knownScenarios">Geçerli senaryo adları.</param>
    /// <param name="allScenarios">"all" yazıldığında çalıştırılacak senaryolar, sırasıyla.</param>
    public ArgumentParser(IReadOnlyCollection<string> knownScenarios, IReadOnlyList<string> allScenarios)
    {
        _knownScenarios = knownScenarios;
        _allScenarios = allScenarios;
    }

    public ParseResult Parse(IReadOnlyList<string> args)
    {
        var options = new LoadTestOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help" or "-?" or "/?")
            {
                return ParseResult.Help();
            }

            if (arg is "--list")
            {
                return ParseResult.List();
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                return ParseResult.Fail($"Beklenmeyen argüman: '{arg}'. Yardım için --help.");
            }

            string name;
            string? value;
            var eq = arg.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                name = arg[2..eq];
                value = arg[(eq + 1)..];
            }
            else
            {
                name = arg[2..];
                value = i + 1 < args.Count ? args[++i] : null;
            }

            if (value is null)
            {
                return ParseResult.Fail($"'--{name}' için değer verilmedi.");
            }

            string? error;
            (options, error) = Apply(options, name, value);
            if (error is not null)
            {
                return ParseResult.Fail(error);
            }
        }

        return ParseResult.Ok(options);
    }

    private (LoadTestOptions Options, string? Error) Apply(LoadTestOptions o, string name, string value)
    {
        switch (name)
        {
            case "scenario":
                return ParseScenarios(value) is { } list
                    ? (o with { Scenarios = list }, null)
                    : (o, $"Bilinmeyen senaryo: '{value}'. Geçerli adlar: {string.Join(", ", _knownScenarios)}, all.");
            case "duration":
                return DurationFormat.TryParse(value, out var duration) && duration > TimeSpan.Zero
                    ? (o with { Duration = duration }, null)
                    : (o, $"Geçersiz süre '{value}' (ör. 30s, 2m).");
            case "warmup":
                return DurationFormat.TryParse(value, out var warmup) && warmup >= TimeSpan.Zero
                    ? (o with { Warmup = warmup }, null)
                    : (o, $"Geçersiz ısınma süresi '{value}' (ör. 5s, 0).");
            case "timeout":
                return DurationFormat.TryParse(value, out var timeout) && timeout > TimeSpan.Zero
                    ? (o with { Timeout = timeout }, null)
                    : (o, $"Geçersiz zaman aşımı '{value}' (ör. 30s).");
            case "concurrency":
                return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var concurrency) && concurrency is >= 1 and <= 10_000
                    ? (o with { Concurrency = concurrency }, null)
                    : (o, $"Eşzamanlılık 1 ile 10000 arasında bir tam sayı olmalı: '{value}'.");
            case "rate":
                return double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate) && rate > 0 && rate <= 1_000_000
                    ? (o with { Rate = rate }, null)
                    : (o, $"Hız saniyede pozitif bir istek sayısı olmalı: '{value}'.");
            case "streaming-url":
                return TryParseUrl(value, out var streaming) ? (o with { StreamingUrl = streaming }, null) : (o, InvalidUrl(name, value));
            case "recording-url":
                return TryParseUrl(value, out var recording) ? (o with { RecordingUrl = recording }, null) : (o, InvalidUrl(name, value));
            case "analysis-url":
                return TryParseUrl(value, out var analysis) ? (o with { AnalysisUrl = analysis }, null) : (o, InvalidUrl(name, value));
            case "output":
                return string.IsNullOrWhiteSpace(value) ? (o, "--output için dosya yolu gerekli.") : (o with { JsonOutputPath = value }, null);
            case "markdown":
                return string.IsNullOrWhiteSpace(value) ? (o, "--markdown için dosya yolu gerekli.") : (o with { MarkdownOutputPath = value }, null);
            default:
                return (o, $"Bilinmeyen seçenek: '--{name}'. Yardım için --help.");
        }
    }

    /// <summary>"all", tek ad ya da virgülle ayrılmış adlar. Tekrarlar bir kez çalıştırılır.</summary>
    private List<string>? ParseScenarios(string value)
    {
        var result = new List<string>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = part.ToLowerInvariant();
            IEnumerable<string> names = name == "all" ? _allScenarios : [name];
            foreach (var n in names)
            {
                if (!_knownScenarios.Contains(n, StringComparer.Ordinal))
                {
                    return null;
                }

                if (!result.Contains(n, StringComparer.Ordinal))
                {
                    result.Add(n);
                }
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static bool TryParseUrl(string value, out Uri uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri!) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string InvalidUrl(string name, string value) =>
        $"--{name} mutlak bir http(s) adresi olmalı: '{value}'.";
}
