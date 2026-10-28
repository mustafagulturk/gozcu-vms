using Gozcu.Media.Processes;

namespace Gozcu.UnitTests.Media;

public class ReconnectPolicyTests
{
    private static readonly ReconnectPolicy Policy = new()
    {
        InitialDelay = TimeSpan.FromSeconds(1),
        MaxDelay = TimeSpan.FromSeconds(30),
        Multiplier = 2,
        JitterRatio = 0.2,
    };

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(100, 30)]
    public void Delay_grows_exponentially_and_is_capped_without_jitter(int attempt, double expectedSeconds)
    {
        // random=0.5 → sapma sıfır
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), Policy.GetDelay(attempt, () => 0.5));
    }

    [Fact]
    public void Jitter_stays_within_configured_ratio()
    {
        Assert.Equal(TimeSpan.FromSeconds(3.2), Policy.GetDelay(3, () => 0.0));
        Assert.Equal(TimeSpan.FromSeconds(4.8), Policy.GetDelay(3, () => 1.0));
    }

    [Fact]
    public void Jitter_never_exceeds_max_delay()
    {
        Assert.Equal(Policy.MaxDelay, Policy.GetDelay(10, () => 1.0));
    }

    [Fact]
    public void Attempt_must_start_from_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy.GetDelay(0));
    }
}
