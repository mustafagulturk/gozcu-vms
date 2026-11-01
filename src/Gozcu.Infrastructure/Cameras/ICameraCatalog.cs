using Gozcu.Contracts.Cameras;

namespace Gozcu.Infrastructure.Cameras;

/// <summary>
/// Servis içindeki kamera listesinin bellekteki kopyası. Kaynak veritabanıdır; kopya açılışta, RabbitMQ
/// <c>camera.catalog.changed</c> olayında ve periyodik olarak yenilenir.
/// </summary>
public interface ICameraCatalog
{
    /// <summary>Tüm kameralar (devre dışı olanlar dahil), kimliğe göre sıralı.</summary>
    IReadOnlyList<CameraDefinition> Cameras { get; }

    /// <summary>Her değişiklikte artan sayaç. İlk yüklemeden önce 0.</summary>
    long Version { get; }

    CameraDefinition? Find(string cameraId);

    /// <summary>İlk yükleme tamamlanınca biter.</summary>
    Task WaitUntilLoadedAsync(CancellationToken cancellationToken);

    /// <summary><paramref name="knownVersion"/>'dan farklı bir sürüm olduğunda yeni sürümle döner.</summary>
    Task<long> WaitForChangeAsync(long knownVersion, CancellationToken cancellationToken);

    /// <summary>Kataloğu hemen veritabanından yeniden okur.</summary>
    Task RefreshAsync(CancellationToken cancellationToken);
}
