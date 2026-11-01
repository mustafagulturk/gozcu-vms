extern alias recording;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gozcu.Infrastructure.Persistence;
using Gozcu.IntegrationTests.Analysis;
using Gozcu.Media.Processes;
using Gozcu.RecordingService;
using Gozcu.RecordingService.Playback;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Gozcu.IntegrationTests;

/// <summary>
/// Kayıttan tek JPEG kare: ana makinedeki ffmpeg ile üretilen gerçek MP4 parçaları, PostgreSQL'deki parça kayıtları ve
/// kayıt servisinin HTTP ucu (WebApplicationFactory).
/// </summary>
public sealed class PlaybackFrameIntegrationTests : IClassFixture<SearchDatabaseFixture>, IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 10, 15, 0, TimeSpan.Zero);

    private readonly SearchDatabaseFixture _db;
    private readonly string _root = Directory.CreateTempSubdirectory("gozcu-it-frame-").FullName;

    public PlaybackFrameIntegrationTests(SearchDatabaseFixture db) => _db = db;

    [Fact]
    public async Task Extractor_returns_scaled_jpeg_and_retries_when_offset_is_past_the_last_frame()
    {
        var path = Path.Combine(_root, "direct", "a.mp4");
        await GenerateSegmentAsync(path, seconds: 2);
        using var extractor = new FrameExtractor(new ProcessOutputReader(), "ffmpeg", NullLogger<FrameExtractor>.Instance);

        var early = await extractor.ExtractJpegAsync(path, TimeSpan.FromSeconds(0.2), 160, CancellationToken.None);
        var late = await extractor.ExtractJpegAsync(path, TimeSpan.FromSeconds(1.6), 160, CancellationToken.None);
        var pastEnd = await extractor.ExtractJpegAsync(path, TimeSpan.FromSeconds(2.5), 160, CancellationToken.None);

        AssertJpeg(early, 160, 120);
        AssertJpeg(late, 160, 120);
        AssertJpeg(pastEnd, 160, 120);
        Assert.NotEqual(early, late); // testsrc her karede değişir: konum gerçekten uygulanmış

        await Assert.ThrowsAsync<FrameExtractionException>(
            () => extractor.ExtractJpegAsync(Path.Combine(_root, "yok.mp4"), TimeSpan.Zero, 160, CancellationToken.None));
    }

    [Fact]
    public async Task Frame_endpoint_serves_cached_jpeg_from_the_segment_containing_the_time()
    {
        var segments = new SegmentRepository(_db.DataSource);
        // 10:15:00–10:15:02 ve 10:15:03–10:15:05 (1 sn boşluk), sonra 10:16:00'da ayrı bir parça (dosyası silinmiş).
        var first = Path.Combine(_root, "frame-cam", "first.mp4");
        var second = Path.Combine(_root, "frame-cam", "second.mp4");
        var missing = Path.Combine(_root, "frame-cam", "missing.mp4");
        await GenerateSegmentAsync(first, seconds: 2);
        await GenerateSegmentAsync(second, seconds: 2);
        await segments.UpsertAsync("frame-cam", first, T0, T0.AddSeconds(2), new FileInfo(first).Length, CancellationToken.None);
        await segments.UpsertAsync("frame-cam", second, T0.AddSeconds(3), T0.AddSeconds(5), new FileInfo(second).Length, CancellationToken.None);
        await segments.UpsertAsync("frame-cam", missing, T0.AddMinutes(1), T0.AddMinutes(2), 1, CancellationToken.None);

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/playback/frame-cam/frame.jpg?time={Iso(T0.AddSeconds(1))}&width=200");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("public, max-age=86400", response.Headers.CacheControl?.ToString());
        AssertJpeg(await response.Content.ReadAsByteArrayAsync(), 200, 150);

        // Varsayılan genişlik 320; saat dilimli zaman.
        var defaultWidth = await client.GetByteArrayAsync($"/api/playback/frame-cam/frame.jpg?time={Iso(T0.AddSeconds(4).ToOffset(TimeSpan.FromHours(3)))}");
        AssertJpeg(defaultWidth, 320, 240);

        // Parçalar arasındaki küçük boşluk: sonraki parçanın ilk karesi.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/playback/frame-cam/frame.jpg?time={Iso(T0.AddSeconds(2.5))}")).StatusCode);

        // Kayıt olmayan an, dosyası olmayan parça ve bilinmeyen kamera: 404 ProblemDetails, önbelleğe alınmaz.
        foreach (var url in new[]
                 {
                     $"/api/playback/frame-cam/frame.jpg?time={Iso(T0.AddSeconds(30))}",
                     $"/api/playback/frame-cam/frame.jpg?time={Iso(T0.AddMinutes(1.5))}",
                     $"/api/playback/yok/frame.jpg?time={Iso(T0.AddSeconds(1))}",
                 })
        {
            var notFound = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
            Assert.Equal("application/problem+json", notFound.Content.Headers.ContentType?.MediaType);
            Assert.Null(notFound.Headers.CacheControl);
        }

        var bad = await client.GetAsync($"/api/playback/frame-cam/frame.jpg?time={Iso(T0)}&width=2000");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("'width'", (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/playback/frame-cam/frame.jpg")).StatusCode);
    }

    private WebApplicationFactory<recording::Program> CreateFactory() =>
        new WebApplicationFactory<recording::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                // Kayıt, indeksleme, saklama ve uzlaştırma çalışmaz; yalnızca HTTP uçları sınanır.
                services.RemoveAll<IHostedService>();
                services.RemoveAll<NpgsqlDataSource>();
                services.AddSingleton(_db.DataSource);
                services.PostConfigure<RecordingOptions>(o => o.RootPath = _root);
            });
        });

    /// <summary>JPEG imzalarını ve SOF başlığındaki boyutları denetler.</summary>
    private static void AssertJpeg(byte[]? jpeg, int width, int height)
    {
        Assert.NotNull(jpeg);
        Assert.True(jpeg.Length > 100, "JPEG boş olmamalı");
        Assert.Equal([0xFF, 0xD8], jpeg[..2]);
        Assert.Equal([0xFF, 0xD9], jpeg[^2..]);

        for (var i = 2; i + 8 < jpeg.Length; i++)
        {
            if (jpeg[i] == 0xFF && jpeg[i + 1] is 0xC0 or 0xC1 or 0xC2)
            {
                Assert.Equal(height, (jpeg[i + 5] << 8) | jpeg[i + 6]);
                Assert.Equal(width, (jpeg[i + 7] << 8) | jpeg[i + 8]);
                return;
            }
        }

        Assert.Fail("JPEG'de SOF başlığı bulunamadı");
    }

    private static async Task GenerateSegmentAsync(string path, int seconds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var startInfo = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y", "-nostdin",
                     "-f", "lavfi", "-i", "testsrc=size=320x240:rate=25",
                     "-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "-c:v", "libx264", "-g", "25", "-pix_fmt", "yuv420p", path,
                 })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"ffmpeg test parçası üretemedi: {error}");
    }

    private static string Iso(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("O"));

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
