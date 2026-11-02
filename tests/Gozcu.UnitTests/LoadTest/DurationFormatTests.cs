using Gozcu.LoadTest.Cli;

namespace Gozcu.UnitTests.LoadTest;

public class DurationFormatTests
{
    [Theory]
    [InlineData("30s", 30_000)]
    [InlineData("30", 30_000)]
    [InlineData("500ms", 500)]
    [InlineData("1.5s", 1_500)]
    [InlineData("2m", 120_000)]
    [InlineData("1h", 3_600_000)]
    [InlineData(" 10S ", 10_000)]
    [InlineData("0", 0)]
    public void Parses_supported_units(string text, double expectedMs)
    {
        Assert.True(DurationFormat.TryParse(text, out var value));
        Assert.Equal(expectedMs, value.TotalMilliseconds, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("s")]
    [InlineData("10x")]
    [InlineData("-5s")]
    [InlineData("1e9999s")]
    public void Rejects_invalid_text(string? text)
    {
        Assert.False(DurationFormat.TryParse(text, out _));
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(250, "250ms")]
    [InlineData(1_500, "1.5s")]
    [InlineData(30_000, "30s")]
    [InlineData(90_000, "90s")]
    [InlineData(120_000, "2m")]
    [InlineData(5_400_000, "90m")]
    [InlineData(7_200_000, "2h")]
    public void Formats_compactly(double ms, string expected)
    {
        Assert.Equal(expected, DurationFormat.Format(TimeSpan.FromMilliseconds(ms)));
    }
}
