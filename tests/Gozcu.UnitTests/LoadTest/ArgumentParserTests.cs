using Gozcu.LoadTest.Cli;
using Gozcu.LoadTest.Scenarios;

namespace Gozcu.UnitTests.LoadTest;

public class ArgumentParserTests
{
    private static ParseResult Parse(params string[] args) =>
        new ArgumentParser(ScenarioCatalog.Names, ScenarioCatalog.AllNames).Parse(args);

    [Fact]
    public void Defaults_are_used_when_no_arguments_given()
    {
        var o = Parse().Options!;

        Assert.Equal(["mixed"], o.Scenarios);
        Assert.Equal(TimeSpan.FromSeconds(30), o.Duration);
        Assert.Equal(TimeSpan.FromSeconds(5), o.Warmup);
        Assert.Equal(16, o.Concurrency);
        Assert.Null(o.Rate);
        Assert.False(o.OpenModel);
        Assert.Equal(new Uri("http://localhost:5081"), o.RecordingUrl);
        Assert.Equal(new Uri("http://localhost:5082"), o.AnalysisUrl);
        Assert.Equal(new Uri("http://localhost:5080"), o.StreamingUrl);
        Assert.Null(o.JsonOutputPath);
        Assert.Null(o.MarkdownOutputPath);
    }

    [Fact]
    public void All_options_are_parsed_in_both_forms()
    {
        var result = Parse(
            "--scenario", "timeline-24h", "--duration=2m", "--concurrency", "64", "--warmup", "0",
            "--rate=250.5", "--recording-url", "http://rec:8080", "--analysis-url=https://an:9000/",
            "--streaming-url", "http://str", "--output", "r.json", "--markdown=r.md", "--timeout", "500ms");

        Assert.Null(result.Error);
        var o = result.Options!;
        Assert.Equal(["timeline-24h"], o.Scenarios);
        Assert.Equal(TimeSpan.FromMinutes(2), o.Duration);
        Assert.Equal(64, o.Concurrency);
        Assert.Equal(TimeSpan.Zero, o.Warmup);
        Assert.Equal(250.5, o.Rate);
        Assert.True(o.OpenModel);
        Assert.Equal(new Uri("http://rec:8080"), o.RecordingUrl);
        Assert.Equal(new Uri("https://an:9000/"), o.AnalysisUrl);
        Assert.Equal(new Uri("http://str"), o.StreamingUrl);
        Assert.Equal("r.json", o.JsonOutputPath);
        Assert.Equal("r.md", o.MarkdownOutputPath);
        Assert.Equal(TimeSpan.FromMilliseconds(500), o.Timeout);
    }

    [Fact]
    public void All_expands_to_every_scenario_except_export()
    {
        var o = Parse("--scenario", "all").Options!;

        Assert.DoesNotContain("export", o.Scenarios);
        Assert.Contains("mixed", o.Scenarios);
        Assert.Contains("segment-media", o.Scenarios);
        Assert.Equal(ScenarioCatalog.Names.Count - 1, o.Scenarios.Count);
    }

    [Fact]
    public void Comma_separated_scenarios_keep_order_and_drop_duplicates()
    {
        var o = Parse("--scenario", "export, locate,EXPORT").Options!;

        Assert.Equal(["export", "locate"], o.Scenarios);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Help_is_recognised(string arg)
    {
        Assert.True(Parse("--duration", "5s", arg).ShowHelp);
    }

    [Fact]
    public void List_is_recognised()
    {
        Assert.True(Parse("--list").ListScenarios);
    }

    [Theory]
    [InlineData("--scenario", "nope")]
    [InlineData("--duration", "0s")]
    [InlineData("--duration", "abc")]
    [InlineData("--warmup", "-1s")]
    [InlineData("--concurrency", "0")]
    [InlineData("--concurrency", "-3")]
    [InlineData("--concurrency", "1.5")]
    [InlineData("--rate", "0")]
    [InlineData("--recording-url", "ftp://x")]
    [InlineData("--analysis-url", "localhost:5082")]
    [InlineData("--bogus", "1")]
    public void Invalid_values_produce_turkish_error(string name, string value)
    {
        var result = Parse(name, value);

        Assert.Null(result.Options);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void Missing_value_is_an_error()
    {
        var result = Parse("--duration");

        Assert.Contains("--duration", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Positional_argument_is_an_error()
    {
        Assert.NotNull(Parse("mixed").Error);
    }
}
