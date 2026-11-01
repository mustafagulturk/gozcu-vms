using System.Net;
using Gozcu.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Gozcu.IntegrationTests.Gateway;

/// <summary>
/// Ağ geçidinin yönlendirme tablosu: tarayıcının tek kökenden (gateway) yaptığı istekler doğru arka uca, doğru yolla
/// gider mi? Arka uç olarak yerelde gerçek Kestrel üzerinde çalışan sahte bir uygulama kullanılır; konteyner gerekmez.
/// </summary>
public sealed class GatewayRoutingIntegrationTests : IAsyncLifetime
{
    private WebApplication? _backend;
    private WebApplicationFactory<GatewayEntryPoint>? _gateway;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _backend = builder.Build();
        // Sahte arka uç gelen isteğin yolunu ve hangi "servise" ulaştığını geri yazar.
        _backend.MapGet("/api/cameras", () => Results.Text("""[{"id":"cam1","health":null}]""", "application/json"));
        _backend.MapMethods("/api/cameras/{**rest}", ["GET", "POST", "PUT", "DELETE"],
            (HttpContext context) => Results.Text($"streaming {context.Request.Method} {context.Request.Path}{context.Request.QueryString} if-match={context.Request.Headers.IfMatch}"));
        _backend.MapGet("/api/playback/{**rest}", (HttpContext context) => Results.Text($"recording {context.Request.Path}{context.Request.QueryString}"));
        _backend.MapGet("/api/analysis/{**rest}", (HttpContext context) => Results.Text($"analysis {context.Request.Path}{context.Request.QueryString}"));
        _backend.MapGet("/{camera}/index.m3u8", (string camera) => Results.Text($"#EXTM3U\n# {camera}\n", "application/vnd.apple.mpegurl"));
        await _backend.StartAsync();

        // Port 0 ile açıldı; gerçek adres sunucu başladıktan sonra bilinir.
        var backendUrl = _backend.Urls.First().TrimEnd('/') + "/";
        _gateway = new WebApplicationFactory<GatewayEntryPoint>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Development");
            web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:Clusters:streaming:Destinations:default:Address"] = backendUrl,
                ["ReverseProxy:Clusters:recording:Destinations:default:Address"] = backendUrl,
                ["ReverseProxy:Clusters:analysis:Destinations:default:Address"] = backendUrl,
                ["ReverseProxy:Clusters:hls:Destinations:default:Address"] = backendUrl,
            }));
        });
    }

    public async Task DisposeAsync()
    {
        if (_gateway is not null)
        {
            await _gateway.DisposeAsync();
        }

        if (_backend is not null)
        {
            await _backend.DisposeAsync();
        }
    }

    [Fact]
    public async Task Forwards_camera_list_without_trailing_path()
    {
        using var client = _gateway!.CreateClient();
        var response = await client.GetAsync(new Uri("/api/cameras", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""[{"id":"cam1","health":null}]""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Forwards_camera_writes_with_method_query_and_if_match()
    {
        using var client = _gateway!.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri("/api/cameras/cam5?x=1", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("If-Match", "\"3\"");
        request.Content = new StringContent("""{"name":"Yeni"}""", System.Text.Encoding.UTF8, "application/json");
        var response = await client.SendAsync(request);
        Assert.Equal("streaming PUT /api/cameras/cam5?x=1 if-match=\"3\"", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/playback/cam1/frame.jpg?time=2026-10-30T10:00:00Z&width=320", "recording /api/playback/cam1/frame.jpg?time=2026-10-30T10:00:00Z&width=320")]
    [InlineData("/api/analysis/events/search?cameraId=cam1&cameraId=cam2", "analysis /api/analysis/events/search?cameraId=cam1&cameraId=cam2")]
    public async Task Forwards_service_apis_to_their_owner(string path, string expected)
    {
        using var client = _gateway!.CreateClient();
        Assert.Equal(expected, await client.GetStringAsync(new Uri(path, UriKind.Relative)));
    }

    [Fact]
    public async Task Forwards_hls_with_prefix_removed()
    {
        using var client = _gateway!.CreateClient();
        var body = await client.GetStringAsync(new Uri("/hls/cam2/index.m3u8", UriKind.Relative));
        Assert.Equal("#EXTM3U\n# cam2\n", body);
    }

    [Theory]
    [InlineData("/", "Canlı")]
    [InlineData("/playback.html", "Kayıt")]
    [InlineData("/events.html", "Olay")]
    [InlineData("/cameras.html", "Kamera")]
    [InlineData("/analysis.html", "Analiz")]
    [InlineData("/panel.css", "--bg")]
    [InlineData("/panel.js", "Gozcu")]
    public async Task Serves_panel_pages(string path, string expected)
    {
        using var client = _gateway!.CreateClient();
        var body = await client.GetStringAsync(new Uri(path, UriKind.Relative));
        Assert.Contains(expected, body, StringComparison.Ordinal);
    }
}
