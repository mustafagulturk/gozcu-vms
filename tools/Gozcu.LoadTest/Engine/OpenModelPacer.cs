namespace Gozcu.LoadTest.Engine;

/// <summary>
/// Açık model zamanlaması: k. isteğin planlanan başlangıcı başlangıç + k/hız'dır. Zamanlayıcı her tetiklendiğinde
/// o ana kadar planlanmış ama başlatılmamış tüm istekler başlatılır; böylece zamanlayıcı çözünürlüğünden
/// (Linux ~1 ms, Windows ~15 ms) bağımsız olarak ortalama hız tutar. Saf hesaplardır.
/// </summary>
internal static class OpenModelPacer
{
    /// <summary>Başlangıçtan <paramref name="elapsedTicks"/> sonra planlanmış toplam istek sayısı (0 anında 1: ilk istek hemen).</summary>
    public static long ScheduledCount(long elapsedTicks, long ticksPerSecond, double rate)
    {
        if (elapsedTicks < 0)
        {
            return 0;
        }

        return (long)Math.Floor(elapsedTicks * rate / ticksPerSecond) + 1;
    }

    /// <summary><paramref name="index"/>. isteğin (0'dan) planlanan zaman damgası.</summary>
    public static long IntendedTimestamp(long startTimestamp, long index, long ticksPerSecond, double rate) =>
        startTimestamp + (long)Math.Round(index * ticksPerSecond / rate);
}
