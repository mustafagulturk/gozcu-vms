extern alias analysis;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gozcu.Contracts.Analysis;
using Gozcu.Contracts.Cameras;
using Gozcu.Infrastructure.Cameras;
using Gozcu.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Gozcu.IntegrationTests.Analysis;

/// <summary>PostgreSQL (şema uygulanmış) konteyneri; sınıftaki testler arasında paylaşılır, her test kendi kamera kimliklerini kullanır.</summary>
public sealed class SearchDatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public DetectionRepository Repository { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        DataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        await new DatabaseMigrator(DataSource, NullLogger<DatabaseMigrator>.Instance).MigrateAsync(CancellationToken.None);
        Repository = new DetectionRepository(DataSource);
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

public sealed class EventSearchIntegrationTests : IClassFixture<SearchDatabaseFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = T0.AddDays(1);

    private readonly SearchDatabaseFixture _db;

    public EventSearchIntegrationTests(SearchDatabaseFixture db) => _db = db;

    private DetectionRepository Repository => _db.Repository;

    [Fact]
    public async Task Keyset_pages_match_in_memory_filtering_for_every_filter_combination()
    {
        var cameras = new[] { "srch-a", "srch-b", "srch-c" };
        var events = GenerateEvents(cameras, 400, seed: 42);
        await Repository.UpsertEventsAsync(events, CancellationToken.None);

        var cases = new List<EventSearchQuery>
        {
            Query(cameras),
            Query(["srch-a"]),
            Query(["srch-a", "srch-c"]) with { Category = ObjectCategory.Person },
            Query(cameras) with { Category = ObjectCategory.Vehicle, MinConfidence = 0.7f },
            Query(cameras) with { From = T0.AddHours(2), To = T0.AddHours(5) },
            Query(cameras) with { From = T0.AddHours(7) },
            Query(cameras) with { To = T0.AddHours(1) },
            Query(cameras) with { MinDuration = TimeSpan.FromSeconds(60) },
            // Süren olaylar "şimdiye kadar" sayılır: Now bir gün sonra olduğundan hepsi bu eşiği geçer.
            Query(cameras) with { MinDuration = TimeSpan.FromHours(10), Category = ObjectCategory.Person },
            Query(["srch-b"]) with
            {
                Category = ObjectCategory.Person, From = T0.AddHours(1), To = T0.AddHours(6), MinConfidence = 0.5f,
                MinDuration = TimeSpan.FromSeconds(20),
            },
        };

        foreach (var query in cases)
        {
            var expected = Expected(events, query).Select(e => e.Id).ToList();
            var actual = new List<Guid>();
            EventSearchCursor? cursor = null;
            var pages = 0;
            do
            {
                var page = await Repository.SearchEventsAsync(query with { After = cursor, Limit = 7 }, CancellationToken.None);
                Assert.True(page.Items.Count <= 7);
                actual.AddRange(page.Items.Select(e => e.Id));
                cursor = page.Next;
                pages++;
                Assert.True(pages < 200, "sayfalama bitmeli");
            }
            while (cursor is not null);

            Assert.True(expected.Count > 0, $"Test verisi bu sorgu için sonuç üretmeli: {query}");
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public async Task Last_page_has_no_cursor_and_ties_on_start_time_are_ordered_by_id()
    {
        // Aynı anda başlayan beş olay: sıralama id DESC ile belirlenir, sayfa sınırında kayıp/tekrar olmaz.
        var events = Enumerable.Range(0, 5)
            .Select(_ => new DetectionEventRecord(Guid.NewGuid(), "srch-tie", ObjectCategory.Person, T0, T0.AddSeconds(5), 1, 0.9f, 3))
            .ToList();
        await Repository.UpsertEventsAsync(events, CancellationToken.None);

        var first = await Repository.SearchEventsAsync(Query(["srch-tie"]) with { Limit = 3 }, CancellationToken.None);
        var second = await Repository.SearchEventsAsync(Query(["srch-tie"]) with { Limit = 3, After = first.Next }, CancellationToken.None);
        var exact = await Repository.SearchEventsAsync(Query(["srch-tie"]) with { Limit = 5 }, CancellationToken.None);

        Assert.NotNull(first.Next);
        Assert.Null(second.Next);
        Assert.Null(exact.Next);
        Assert.Equal(
            events.Select(e => e.Id).OrderByDescending(id => id.ToString(), StringComparer.Ordinal),
            first.Items.Concat(second.Items).Select(e => e.Id));
    }

    [Fact]
    public async Task New_events_arriving_between_pages_do_not_shift_the_next_page()
    {
        var events = Enumerable.Range(0, 6)
            .Select(i => new DetectionEventRecord(Guid.NewGuid(), "srch-live", ObjectCategory.Vehicle, T0.AddMinutes(i), T0.AddMinutes(i).AddSeconds(4), 1, 0.9f, 3))
            .ToList();
        await Repository.UpsertEventsAsync(events, CancellationToken.None);

        var first = await Repository.SearchEventsAsync(Query(["srch-live"]) with { Limit = 3 }, CancellationToken.None);
        await Repository.UpsertEventsAsync(
            [new DetectionEventRecord(Guid.NewGuid(), "srch-live", ObjectCategory.Vehicle, T0.AddHours(1), null, 1, 0.9f, 1)], CancellationToken.None);
        var second = await Repository.SearchEventsAsync(Query(["srch-live"]) with { Limit = 3, After = first.Next }, CancellationToken.None);

        Assert.Equal([T0.AddMinutes(5), T0.AddMinutes(4), T0.AddMinutes(3)], first.Items.Select(e => e.StartedAt));
        Assert.Equal([T0.AddMinutes(2), T0.AddMinutes(1), T0], second.Items.Select(e => e.StartedAt));
    }

    [Fact]
    public async Task Frames_are_returned_oldest_first_within_half_open_range_and_limit()
    {
        var frames = Enumerable.Range(0, 30)
            .Select(i => new DetectionFrameRecord("srch-frames", T0.AddMilliseconds(500 * i), 10, [Person(0.5f + (i / 100f))]))
            .Append(new DetectionFrameRecord("srch-other", T0.AddSeconds(1), 10, [Person(0.9f)]))
            .ToList();
        await Repository.InsertFramesAsync(frames, CancellationToken.None);

        var found = await Repository.FindFramesAscendingAsync("srch-frames", T0.AddSeconds(2), T0.AddSeconds(7), 100, CancellationToken.None);
        Assert.Equal(Enumerable.Range(4, 10).Select(i => T0.AddMilliseconds(500 * i)), found.Select(f => f.CapturedAt));
        Assert.Equal(frames[4].Detections, found[0].Detections);

        var limited = await Repository.FindFramesAscendingAsync("srch-frames", T0, T0.AddMinutes(1), 3, CancellationToken.None);
        Assert.Equal([T0, T0.AddMilliseconds(500), T0.AddSeconds(1)], limited.Select(f => f.CapturedAt));
    }

    [Fact]
    public async Task Http_api_returns_contract_json_with_camera_names_and_problem_details()
    {
        await Repository.UpsertEventsAsync(
        [
            new(Guid.NewGuid(), "http-cam1", ObjectCategory.Person, T0, T0.AddSeconds(12.5), 3, 0.93f, 20),
            new(Guid.NewGuid(), "http-cam2", ObjectCategory.Vehicle, T0.AddMinutes(1), null, 1, 0.61f, 4),
            new(Guid.NewGuid(), "http-gone", ObjectCategory.Person, T0.AddMinutes(2), T0.AddMinutes(3), 1, 0.7f, 9),
        ], CancellationToken.None);
        await Repository.InsertFramesAsync(
        [
            new("http-cam1", T0.AddSeconds(2), 10, [new StoredDetection("car", ObjectCategory.Vehicle, 0.91f, 0.1f, 0.2f, 0.3f, 0.4f)]),
            new("http-cam1", T0.AddSeconds(1), 10, [Person(0.8f)]),
        ], CancellationToken.None);

        var clock = new FakeTimeProvider(T0.AddMinutes(1).AddSeconds(30));
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();

        // İlk sayfa: en yeni iki olay, sonraki sayfa imleci.
        var response = await client.GetAsync("/api/analysis/events/search?cameraId=http-cam1&cameraId=http-cam2,http-gone&limit=2");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var page1 = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = page1.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());

        var gone = items[0];
        Assert.Equal(
            ["id", "cameraId", "cameraName", "category", "startedAt", "endedAt", "durationSeconds", "maxCount", "peakConfidence", "frameCount"],
            gone.EnumerateObject().Select(p => p.Name));
        Assert.Equal("http-gone", gone.GetProperty("cameraName").GetString()); // katalogda yok: kimlik
        Assert.Equal(60, gone.GetProperty("durationSeconds").GetDouble());

        var ongoing = items[1];
        Assert.Equal("Otopark Girişi", ongoing.GetProperty("cameraName").GetString());
        Assert.Equal("Vehicle", ongoing.GetProperty("category").GetString());
        Assert.Equal(JsonValueKind.Null, ongoing.GetProperty("endedAt").ValueKind);
        Assert.Equal(30, ongoing.GetProperty("durationSeconds").GetDouble()); // şimdiye kadar
        Assert.Equal(T0.AddMinutes(1), ongoing.GetProperty("startedAt").GetDateTimeOffset());

        var cursor = page1.RootElement.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        var page2 = await client.GetFromJsonAsync<JsonElement>(
            $"/api/analysis/events/search?cameraId=http-cam1&cameraId=http-cam2&cameraId=http-gone&limit=2&cursor={Uri.EscapeDataString(cursor!)}");
        var last = Assert.Single(page2.GetProperty("items").EnumerateArray());
        Assert.Equal("Kapı", last.GetProperty("cameraName").GetString());
        Assert.Equal(12.5, last.GetProperty("durationSeconds").GetDouble());
        Assert.Equal(0.93f, last.GetProperty("peakConfidence").GetSingle());
        Assert.Equal(JsonValueKind.Null, page2.GetProperty("nextCursor").ValueKind);

        // Filtreler: kategori + süre.
        var filtered = await client.GetFromJsonAsync<JsonElement>(
            "/api/analysis/events/search?cameraId=http-cam1&cameraId=http-cam2&cameraId=http-gone&category=Person&minDurationSeconds=20");
        Assert.Equal("http-gone", Assert.Single(filtered.GetProperty("items").EnumerateArray()).GetProperty("cameraId").GetString());

        // Geçersiz parametre: 400 ProblemDetails.
        var bad = await client.GetAsync("/api/analysis/events/search?limit=500");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("application/problem+json", bad.Content.Headers.ContentType?.MediaType);
        var problem = await bad.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("'limit'", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/analysis/events/search?cursor=bozuk")).StatusCode);

        // Kareler: eskiden yeniye, sözleşmedeki alan adlarıyla.
        var frames = await client.GetFromJsonAsync<JsonElement>(
            $"/api/analysis/cameras/http-cam1/frames?from={Iso(T0)}&to={Iso(T0.AddMinutes(1))}");
        Assert.Equal(2, frames.GetArrayLength());
        Assert.Equal(["capturedAt", "detections"], frames[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(T0.AddSeconds(1), frames[0].GetProperty("capturedAt").GetDateTimeOffset());
        var car = frames[1].GetProperty("detections")[0];
        Assert.Equal(["label", "category", "confidence", "x", "y", "w", "h"], car.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Vehicle", car.GetProperty("category").GetString());
        Assert.Equal(0.3f, car.GetProperty("w").GetSingle());

        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>(
            $"/api/analysis/cameras/yok/frames?from={Iso(T0)}&to={Iso(T0.AddMinutes(1))}")).GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(
            $"/api/analysis/cameras/http-cam1/frames?from={Iso(T0)}&to={Iso(T0.AddMinutes(16))}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/analysis/cameras/http-cam1/frames")).StatusCode);
    }

    private WebApplicationFactory<analysis::Program> CreateFactory(TimeProvider clock) =>
        new WebApplicationFactory<analysis::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                // Yalnızca HTTP uçları sınanır: analiz hattı, yazıcı, göç ve katalog yenileyici gibi arka plan işleri çalışmaz.
                services.RemoveAll<IHostedService>();
                services.RemoveAll<NpgsqlDataSource>();
                services.AddSingleton(_db.DataSource);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(clock);
                services.RemoveAll<ICameraCatalog>();
                services.AddSingleton<ICameraCatalog>(new FakeCameraCatalog(
                    new CameraDefinition { Id = "http-cam1", Name = "Kapı" },
                    new CameraDefinition { Id = "http-cam2", Name = "Otopark Girişi", Enabled = false }));
            });
        });

    private static EventSearchQuery Query(IReadOnlyList<string> cameras) =>
        new(cameras, null, null, null, null, null, Now, null, 50);

    private static IEnumerable<DetectionEventRecord> Expected(IEnumerable<DetectionEventRecord> events, EventSearchQuery q) =>
        events
            .Where(e => q.CameraIds.Count == 0 || q.CameraIds.Contains(e.CameraId))
            .Where(e => q.Category is null || e.Category == q.Category)
            .Where(e => q.From is null || e.EndedAt is null || e.EndedAt >= q.From)
            .Where(e => q.To is null || e.StartedAt < q.To)
            .Where(e => q.MinConfidence is null || e.PeakConfidence >= q.MinConfidence)
            .Where(e => q.MinDuration is null || (e.EndedAt ?? q.Now) - e.StartedAt >= q.MinDuration)
            .OrderByDescending(e => e.StartedAt)
            .ThenByDescending(e => e.Id.ToString(), StringComparer.Ordinal); // PostgreSQL uuid sırası = metin sırası

    private static List<DetectionEventRecord> GenerateEvents(string[] cameras, int count, int seed)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count)
            .Select(_ =>
            {
                // Dakikaya yuvarlanmış başlangıçlar: aynı anda başlayan olaylar sık olur (id ile sıralama sınanır).
                var start = T0.AddMinutes(random.Next(0, 8 * 60 / 2) * 2);
                DateTimeOffset? end = random.NextDouble() < 0.1 ? null : start.AddMilliseconds(random.Next(0, 180_000));
                return new DetectionEventRecord(
                    Guid.NewGuid(),
                    cameras[random.Next(cameras.Length)],
                    random.Next(2) == 0 ? ObjectCategory.Person : ObjectCategory.Vehicle,
                    start,
                    end,
                    random.Next(1, 5),
                    MathF.Round((float)random.NextDouble(), 3),
                    random.Next(1, 100));
            })
            .ToList();
    }

    private static string Iso(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("O"));

    private static StoredDetection Person(float confidence) => new("person", ObjectCategory.Person, confidence, 0.5f, 0.5f, 0.1f, 0.3f);
}

/// <summary>Sabit kamera listesiyle sahte katalog.</summary>
internal sealed class FakeCameraCatalog(params CameraDefinition[] cameras) : ICameraCatalog
{
    public IReadOnlyList<CameraDefinition> Cameras { get; } = cameras.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();

    public long Version => 1;

    public CameraDefinition? Find(string cameraId) => Cameras.FirstOrDefault(c => c.Id == cameraId);

    public Task WaitUntilLoadedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<long> WaitForChangeAsync(long knownVersion, CancellationToken cancellationToken) =>
        knownVersion != Version ? Task.FromResult(Version) : Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => Version, TaskScheduler.Default);

    public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
