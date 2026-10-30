namespace Gozcu.Contracts.Cameras;

/// <summary>
/// Bir kameranın sistemdeki tanımı. 1. aşamada yapılandırmadan okunur,
/// ileride API servisi üzerinden veritabanında yönetilecektir.
/// </summary>
public sealed class CameraDefinition
{
    /// <summary>Kısa, URL ve dosya adı güvenli kimlik (ör. "cam1").</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public Uri? RtspUrl { get; set; }

    public bool Enabled { get; set; } = true;
}
