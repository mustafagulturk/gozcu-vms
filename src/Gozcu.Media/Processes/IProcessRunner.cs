namespace Gozcu.Media.Processes;

/// <summary>Harici süreç çalıştırma soyutlaması. Birim testlerde sahtesiyle değiştirilir.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Süreci başlatır ve çıkana kadar bekler. İptal edildiğinde süreç önce nazikçe, gerekirse zorla durdurulur.
    /// </summary>
    /// <returns>Sürecin çıkış kodu.</returns>
    Task<int> RunAsync(
        ProcessSpec spec,
        Action<string> onStandardOutput,
        Action<string> onStandardError,
        CancellationToken cancellationToken);
}
