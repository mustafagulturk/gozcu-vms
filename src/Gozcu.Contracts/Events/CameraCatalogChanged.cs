namespace Gozcu.Contracts.Events;

public enum CameraChangeKind
{
    Created,
    Updated,
    Deleted,

    /// <summary>Boş veritabanı yapılandırmadaki kameralarla ilk kez dolduruldu.</summary>
    Seeded,
}

/// <summary>
/// Akış servisi kamera kataloğunda (veritabanındaki <c>cameras</c> tablosu) bir değişiklik yaptığında yayınlar.
/// Diğer servisler olayın içeriğine güvenmek yerine kataloğu veritabanından yeniden okur; olay yalnızca
/// "yeniden oku" sinyalidir. Olay kaybolursa periyodik yenileme farkı kapatır.
/// </summary>
public sealed record CameraCatalogChanged(string? CameraId, CameraChangeKind Kind, int? Version) : IntegrationEvent
{
    public override string RoutingKey => EventRoutingKeys.CameraCatalogChanged;
}
