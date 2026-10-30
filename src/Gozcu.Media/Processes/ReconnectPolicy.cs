namespace Gozcu.Media.Processes;

/// <summary>
/// Kopan bağlantılar için üstel geri çekilme (exponential backoff) politikası.
/// Gecikme: min(Max, Initial * Multiplier^(deneme-1)) ± Jitter.
/// </summary>
public sealed class ReconnectPolicy
{
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    public double Multiplier { get; init; } = 2.0;

    /// <summary>0 ile 1 arasında; gecikmeye eklenen rastgele sapma oranı.</summary>
    public double JitterRatio { get; init; } = 0.2;

    /// <summary>Süreç bu süreden uzun çalıştıysa bağlantı sağlıklı sayılır ve deneme sayacı sıfırlanır.</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <param name="attempt">1'den başlayan ardışık başarısız deneme sayısı.</param>
    /// <param name="random">0 ile 1 arasında değer üreten kaynak; testlerde sabitlenebilir.</param>
    public TimeSpan GetDelay(int attempt, Func<double>? random = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        var exponent = Math.Min(attempt - 1, 30);
        var baseMs = Math.Min(
            MaxDelay.TotalMilliseconds,
            InitialDelay.TotalMilliseconds * Math.Pow(Multiplier, exponent));

        var sample = (random ?? Random.Shared.NextDouble)();
        var jitter = baseMs * JitterRatio * ((sample * 2) - 1);
        var delayMs = Math.Clamp(baseMs + jitter, 0, MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(delayMs);
    }
}
