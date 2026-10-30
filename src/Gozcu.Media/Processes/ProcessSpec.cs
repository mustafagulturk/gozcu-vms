namespace Gozcu.Media.Processes;

/// <summary>Çalıştırılacak harici sürecin tanımı.</summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments)
{
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Süreç durdurulurken stdin'e yazılacak komut (ffmpeg için "q").
    /// Böylece kapsayıcı (mp4) düzgün kapatılır ve son kayıt parçası bozulmaz.
    /// </summary>
    public string? GracefulStopInput { get; init; }

    /// <summary>Nazik durdurmadan sonra süreci zorla sonlandırmadan önce beklenecek süre.</summary>
    public TimeSpan GracefulStopTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public override string ToString() => $"{FileName} {string.Join(' ', Arguments)}";
}
