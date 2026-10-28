namespace Gozcu.Contracts.Cameras;

public enum CameraHealthState
{
    /// <summary>Henüz bağlantı kurulmadı ya da yeniden bağlanılıyor.</summary>
    Connecting,

    /// <summary>Akış gerçek zamanlı olarak alınıyor.</summary>
    Online,

    /// <summary>Bağlantı açık ama akış gerçek zamanın gerisinde ya da kesik kesik.</summary>
    Degraded,

    /// <summary>Bağlantı koptu; yeniden deneme bekleniyor.</summary>
    Offline,
}
