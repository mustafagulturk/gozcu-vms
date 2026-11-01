extern alias streaming;

using System.Net;
using Microsoft.AspNetCore.Hosting;
using System.Net.Http.Json;
using System.Text.Json;
using Gozcu.Contracts.Cameras;
using Gozcu.Contracts.Events;
using Gozcu.Infrastructure.Cameras;
using Gozcu.Infrastructure.Messaging;
using Gozcu.Infrastructure.Persistence;
using Gozcu.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Gozcu.IntegrationTests.Cameras;

/// <summary>Akış servisi gerçek PostgreSQL ve RabbitMQ ile; yapılandırmada tek bir tohum kamera var.</summary>
public sealed class CameraApiFixture : IAsyncLifetime
{
    public const string SeedCameraId = "tohum";

    public CameraBackendFixture Backend { get; } = new();

    public WebApplicationFactory<streaming::Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Backend.InitializeAsync();
        Factory = new WebApplicationFactory<streaming::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<NpgsqlDataSource>();
                services.AddSingleton(Backend.DataSource);
                services.PostConfigure<RabbitMqOptions>(o => o.ConnectionString = Backend.RabbitUri);
                // Olay kaybını değil olay yolunu sınıyoruz: periyodik yenileme test süresince devreye girmez.
                services.PostConfigure<CameraCatalogOptions>(o => o.RefreshInterval = TimeSpan.FromMinutes(10));
                services.PostConfigure<CameraOptions>(o =>
                {
                    o.Items.Clear();
                    o.Items.Add(new CameraDefinition
                    {
                        Id = SeedCameraId,
                        Name = "Tohum",
                        RtspUrl = new Uri("rtsp://admin:tohumparola@127.0.0.1:1/tohum"),
                        Enabled = false,
                    });
                });
            });
        });
        _ = Factory.Server;
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Backend.DisposeAsync();
    }
}

[Collection(MediaMtxCollection.Name)]
public sealed class CameraManagementApiIntegrationTests(MediaMtxFixture mediaMtx, CameraApiFixture api) : IClassFixture<CameraApiFixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private HttpClient Client => api.Factory.CreateClient();

    [Fact]
    public async Task Empty_database_is_seeded_from_configuration_once()
    {
        var client = Client;
        JsonElement? seeded = null;
        await Eventually.TrueAsync(
            () =>
            {
                var list = client.GetFromJsonAsync<JsonElement>("/api/cameras").GetAwaiter().GetResult();
                seeded = list.EnumerateArray().Cast<JsonElement?>().FirstOrDefault(c => c!.Value.GetProperty("id").GetString() == CameraApiFixture.SeedCameraId);
                return seeded is not null;
            },
            Timeout,
            "tohum kamera listelenmeli");

        var camera = seeded!.Value;
        Assert.Equal("Tohum", camera.GetProperty("name").GetString());
        Assert.Equal("rtsp://admin:***@127.0.0.1:1/tohum", camera.GetProperty("rtspUrl").GetString());
        Assert.False(camera.GetProperty("enabled").GetBoolean());
        Assert.Equal(1, camera.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.Null, camera.GetProperty("health").ValueKind);
        Assert.DoesNotContain("tohumparola", await client.GetStringAsync("/api/cameras"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_update_and_delete_with_etags_publish_catalog_events()
    {
        var client = Client;
        await using var events = await api.Backend.SubscribeAsync();
        var id = NewId("crud");

        // Ekle (devre dışı: izleme süreci başlamasın).
        var created = await client.PostAsJsonAsync("/api/cameras", new { id, name = "Kapı", rtspUrl = "rtsp://admin:gizli@10.0.0.5:554/s1", enabled = false });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"/api/cameras/{id}", created.Headers.Location?.OriginalString);
        Assert.Equal("\"1\"", created.Headers.ETag?.Tag);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rtsp://admin:***@10.0.0.5:554/s1", body.GetProperty("rtspUrl").GetString());
        Assert.Equal(1, body.GetProperty("version").GetInt32());
        Assert.True(body.TryGetProperty("createdAt", out _));
        Assert.True(body.TryGetProperty("updatedAt", out _));
        var createdEvent = await events.WaitForAsync(e => e.CameraId == id, Timeout);
        Assert.Equal((CameraChangeKind.Created, (int?)1), (createdEvent.Kind, createdEvent.Version));

        // Yerel katalog yanıt dönmeden yenilenmiş olmalı.
        var catalog = api.Factory.Services.GetRequiredService<ICameraCatalog>();
        Assert.Equal("Kapı", catalog.Find(id)?.Name);

        var get = await client.GetAsync($"/api/cameras/{id}");
        Assert.Equal("\"1\"", get.Headers.ETag?.Tag);

        // Ad değişir, adres (rtspUrl boş) korunur.
        var renamed = await SendAsync(client, HttpMethod.Put, $"/api/cameras/{id}", new { name = "Ana kapı", enabled = false }, "\"1\"");
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("\"2\"", renamed.Headers.ETag?.Tag);
        body = await renamed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Ana kapı", body.GetProperty("name").GetString());
        Assert.Equal("rtsp://admin:***@10.0.0.5:554/s1", body.GetProperty("rtspUrl").GetString());
        Assert.Equal("Ana kapı", catalog.Find(id)?.Name);

        // Eski sürümle yazma: 412 ve güncel sürüm.
        var stale = await SendAsync(client, HttpMethod.Put, $"/api/cameras/{id}", new { name = "Çakışan", enabled = true }, "\"1\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(2, (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("currentVersion").GetInt32());

        // Gizlenmiş parolayla yeni sunucu: kayıtlı parola korunur.
        var moved = await SendAsync(client, HttpMethod.Put, $"/api/cameras/{id}", new { name = "Ana kapı", rtspUrl = "rtsp://admin:***@10.0.0.6:554/s2", enabled = false }, null);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var stored = await new CameraRepository(api.Backend.DataSource).FindAsync(id, CancellationToken.None);
        Assert.Equal("rtsp://admin:gizli@10.0.0.6:554/s2", stored!.RtspUrl);
        Assert.Equal(3, stored.Version);
        await events.WaitForAsync(e => e.CameraId == id && e.Kind == CameraChangeKind.Updated && e.Version == 3, Timeout);

        // Sil: yanlış sürüm 412, doğru sürüm 204, sonra 404.
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await SendAsync(client, HttpMethod.Delete, $"/api/cameras/{id}", null, "\"2\"")).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await SendAsync(client, HttpMethod.Delete, $"/api/cameras/{id}", null, "\"abc\"")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, HttpMethod.Delete, $"/api/cameras/{id}", null, "\"3\"")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/cameras/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/cameras/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, HttpMethod.Put, $"/api/cameras/{id}", new { name = "x" }, null)).StatusCode);
        Assert.Null(catalog.Find(id));
        await events.WaitForAsync(e => e.CameraId == id && e.Kind == CameraChangeKind.Deleted, Timeout);
    }

    [Fact]
    public async Task Invalid_and_duplicate_cameras_are_rejected()
    {
        var client = Client;

        var invalid = await client.PostAsJsonAsync("/api/cameras", new { id = "kamera 1", name = " ", rtspUrl = "http://h/a" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("id", out _));
        Assert.True(errors.TryGetProperty("name", out _));
        Assert.True(errors.TryGetProperty("rtspUrl", out _));

        var masked = await client.PostAsJsonAsync("/api/cameras", new { id = NewId("maske"), name = "M", rtspUrl = "rtsp://a:***@h/a" });
        Assert.Equal(HttpStatusCode.BadRequest, masked.StatusCode);

        var id = NewId("cift");
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/cameras", new { id, name = "A", rtspUrl = "rtsp://h/a", enabled = false })).StatusCode);
        var duplicate = await client.PostAsJsonAsync("/api/cameras", new { id = id.ToUpperInvariant(), name = "B", rtspUrl = "rtsp://h/b" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("application/problem+json", duplicate.Content.Headers.ContentType?.MediaType);

        var badUpdate = await SendAsync(client, HttpMethod.Put, $"/api/cameras/{id}", new { name = "", rtspUrl = "rtsp:/eksik" }, null);
        Assert.Equal(HttpStatusCode.BadRequest, badUpdate.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/cameras/{id}")).StatusCode);
    }

    [Fact]
    public async Task Added_camera_is_monitored_and_removed_camera_stops_being_monitored()
    {
        var client = Client;
        var id = NewId("canli");
        await using var publisher = mediaMtx.CreatePublisher(id);
        publisher.Start();

        var created = await client.PostAsJsonAsync("/api/cameras", new { id, name = "Canlı", rtspUrl = mediaMtx.CameraUrl(id).ToString() });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        await Eventually.TrueAsync(() => HealthState(client, id) == "Online", Timeout, "yeni kamera izlenmeye başlanmalı");
        var view = await client.GetFromJsonAsync<JsonElement>($"/api/cameras/{id}");
        Assert.Equal("Canlı", view.GetProperty("health").GetProperty("name").GetString());
        Assert.EndsWith($"/{id}/index.m3u8", view.GetProperty("health").GetProperty("hlsUrl").GetString(), StringComparison.Ordinal);

        // Devre dışı bırakılınca izleme durur ve sağlık bilgisi kalkar.
        var registry = api.Factory.Services.GetRequiredService<streaming::Gozcu.StreamingService.Health.CameraRegistry>();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, $"/api/cameras/{id}", new { name = "Canlı", enabled = false }, null)).StatusCode);
        await Eventually.TrueAsync(() => registry.Find(id) is null, Timeout, "devre dışı kameranın izlemesi durmalı");
        Assert.Equal(JsonValueKind.Null, (await client.GetFromJsonAsync<JsonElement>($"/api/cameras/{id}")).GetProperty("health").ValueKind);

        // Yeniden etkinleşince geri gelir; silinince tamamen gider.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, $"/api/cameras/{id}", new { name = "Canlı", enabled = true }, null)).StatusCode);
        await Eventually.TrueAsync(() => HealthState(client, id) == "Online", Timeout, "yeniden etkinleşen kamera izlenmeli");
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/cameras/{id}")).StatusCode);
        await Eventually.TrueAsync(() => registry.Find(id) is null, Timeout, "silinen kameranın izlemesi durmalı");
    }

    [Fact]
    public async Task Probe_reports_stream_properties_or_error_without_password()
    {
        var client = Client;
        const string path = "probe-test";
        await using var publisher = mediaMtx.CreatePublisher(path);
        publisher.Start();

        JsonElement ok = default;
        await Eventually.TrueAsync(
            () =>
            {
                var response = client.PostAsJsonAsync("/api/cameras/probe", new { rtspUrl = mediaMtx.CameraUrl(path).ToString() }).GetAwaiter().GetResult();
                ok = response.Content.ReadFromJsonAsync<JsonElement>().GetAwaiter().GetResult();
                return ok.GetProperty("ok").GetBoolean();
            },
            Timeout,
            "yayındaki kameraya bağlantı testi başarılı olmalı");
        Assert.Equal("h264", ok.GetProperty("codec").GetString());
        Assert.Equal(640, ok.GetProperty("width").GetInt32());
        Assert.Equal(360, ok.GetProperty("height").GetInt32());
        Assert.Equal(25, ok.GetProperty("fps").GetDouble());

        var failed = await client.PostAsJsonAsync("/api/cameras/probe", new { rtspUrl = mediaMtx.CameraUrl("yok").ToString().Replace("rtsp://", "rtsp://admin:cokgizli@", StringComparison.Ordinal) });
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        var error = await failed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(error.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrEmpty(error.GetProperty("error").GetString()));
        Assert.DoesNotContain("cokgizli", error.GetProperty("error").GetString()!, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/cameras/probe", new { rtspUrl = "ftp://h/a" })).StatusCode);
    }

    private static string NewId(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..6];

    private static string? HealthState(HttpClient client, string id)
    {
        var camera = client.GetFromJsonAsync<JsonElement>($"/api/cameras/{id}").GetAwaiter().GetResult();
        var health = camera.GetProperty("health");
        return health.ValueKind == JsonValueKind.Null ? null : health.GetProperty("state").GetString();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, object? body, string? ifMatch)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request);
    }
}
