namespace Gozcu.Gateway;

/// <summary>
/// Testlerde <c>WebApplicationFactory&lt;GatewayEntryPoint&gt;</c> için işaret tipi. Servislerin hepsi genel ad alanında
/// <c>Program</c> sınıfı tanımladığından, birden çok servise başvuran test projesinde <c>Program</c> belirsiz kalır.
/// </summary>
public sealed class GatewayEntryPoint
{
    private GatewayEntryPoint()
    {
    }
}
