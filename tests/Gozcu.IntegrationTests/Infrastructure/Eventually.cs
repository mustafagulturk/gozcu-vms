namespace Gozcu.IntegrationTests.Infrastructure;

public static class Eventually
{
    public static async Task TrueAsync(Func<bool> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"{timeout.TotalSeconds} sn içinde gerçekleşmedi: {because}");
    }
}
