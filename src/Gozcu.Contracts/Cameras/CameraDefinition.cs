namespace Gozcu.Contracts.Cameras;

/// <summary>
/// Bir kameranın sistemdeki tanımı. Kaynak veritabanındaki <c>cameras</c> tablosudur (akış servisinin yönetim
/// API'si yazar); servisler bu tanımı kamera kataloğundan okur.
/// </summary>
public sealed class CameraDefinition
{
    /// <summary>Kısa, URL ve dosya adı güvenli kimlik (ör. "cam1").</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public Uri? RtspUrl { get; set; }

    public bool Enabled { get; set; } = true;
}
