using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Gozcu.IntegrationTests.Infrastructure;

/// <summary>
/// Sahte IP kamera ortamı: MediaMTX konteyneri + ana makinede çalışan ffmpeg yayıncısı.
/// Yayıncı testler tarafından durdurulup başlatılarak kamera kopması canlandırılır.
/// </summary>
public sealed class MediaMtxFixture : IAsyncLifetime
{
    public const string Image = "bluenviron/mediamtx:1.21.3-ffmpeg";
    private const int RtspPort = 8554;

    private readonly IContainer _container = new ContainerBuilder(Image)
        .WithPortBinding(RtspPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(@"\[RTSP\].*:8554"))
        .Build();

    public Uri CameraUrl(string path) => new($"rtsp://{_container.Hostname}:{_container.GetMappedPublicPort(RtspPort)}/{path}");

    public FakeCameraPublisher CreatePublisher(string path) => new(CameraUrl(path));

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class MediaMtxCollection : ICollectionFixture<MediaMtxFixture>
{
    public const string Name = "MediaMTX";
}
