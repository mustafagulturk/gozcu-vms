using Gozcu.Media.Ffmpeg;
using Gozcu.Media.Processes;

namespace Gozcu.Media.Frames;

/// <summary>
/// Çözülmüş ham kare kaynağı. <see cref="IProcessRunner"/> satır tabanlıdır; bu soyutlama ise stdout'tan ikili
/// (binary) kare okur. Tek bir çalıştırmayı temsil eder: yeniden bağlanma döngüsü çağıranın sorumluluğundadır.
/// Birim testlerde sahtesiyle değiştirilir.
/// </summary>
public interface IFrameSource
{
    /// <summary>
    /// Kaynağı başlatır ve akış bitene kadar kareleri <paramref name="handler"/>'a iletir.
    /// İptal edildiğinde kaynak durdurulur ve <see cref="OperationCanceledException"/> fırlatılır.
    /// </summary>
    /// <returns>Kaynak sürecin çıkış kodu.</returns>
    Task<int> RunAsync(FrameSourceRequest request, IFrameSourceHandler handler, CancellationToken cancellationToken);
}

/// <summary>Kaynağın çalıştıracağı süreç ve üreteceği karelerin boyutu.</summary>
public sealed record FrameSourceRequest(ProcessSpec Process, int FrameWidth, int FrameHeight)
{
    /// <summary>BGR24 kare boyutu (bayt).</summary>
    public int FrameSize => FrameWidth * FrameHeight * 3;
}

/// <summary>Kare kaynağının olaylarını alan arayüz. Çağrılar farklı iş parçacıklarından gelebilir.</summary>
public interface IFrameSourceHandler
{
    /// <summary>Giriş akışının bilgisi okunduğunda (çalıştırma başına en fazla bir kez) çağrılır.</summary>
    void OnStreamInfo(StreamInfo info);

    /// <summary>Kaynağın hata gibi görünen bir tanı satırı.</summary>
    void OnErrorLine(string line);

    /// <summary>
    /// Tam bir kare okunduğunda çağrılır. Dizi çağırana devredilir; kaynak onu bir daha kullanmaz.
    /// Çağrı hızlı dönmelidir: kaynak bu sırada bir sonraki kareyi okumaz.
    /// </summary>
    void OnFrame(byte[] frame);
}
