using System.Text.Json.Serialization;
using Gozcu.Contracts.Cameras;
using Gozcu.Infrastructure;
using Gozcu.Infrastructure.Persistence;
using Gozcu.Media.Processes;
using Gozcu.RecordingService;
using Gozcu.RecordingService.Indexing;
using Gozcu.RecordingService.Playback;
using Gozcu.RecordingService.Reconciliation;
using Gozcu.RecordingService.Recording;
using Gozcu.RecordingService.Retention;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<CameraOptions>(o => builder.Configuration.GetSection(CameraOptions.SectionName).Bind(o.Items));
builder.Services.Configure<RecordingOptions>(builder.Configuration.GetSection(RecordingOptions.SectionName));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<SegmentQueue>();
builder.Services.AddSingleton<RecorderRegistry>();
builder.Services.AddSingleton<RecordingMetrics>();
builder.Services.AddGozcuPersistence(builder.Configuration);
builder.Services.AddGozcuMessaging(builder.Configuration);
// Uzlaştırma, kayıt süreçleri yeni dosya açmadan önce diskteki mevcut parçaların listesini alır.
builder.Services.AddStartupReconciliation();
builder.Services.AddHostedService<RecorderService>();
builder.Services.AddHostedService<SegmentIndexer>();
builder.Services.AddHostedService<RetentionService>();
builder.Services.AddPlayback();

var app = builder.Build();

var cameraErrors = app.Services.GetRequiredService<IOptions<CameraOptions>>().Value.Validate();
if (cameraErrors.Count > 0)
{
    throw new InvalidOperationException("Kamera yapılandırması hatalı:\n" + string.Join('\n', cameraErrors));
}

var api = app.MapGroup("/api/recordings");
api.MapGet("/status", (RecorderRegistry recorders, RecordingMetrics metrics) =>
    new { Recorders = recorders.Statuses(), Metrics = metrics.Snapshot() });

// Saate göre arama: ?cameraId=cam1&from=2026-10-28T10:00:00Z&to=2026-10-28T11:00:00Z
api.MapGet("/segments", async (
    string? cameraId, DateTimeOffset? from, DateTimeOffset? to, int? limit,
    SegmentRepository repository, CancellationToken cancellationToken) =>
    await repository.FindAsync(cameraId, from, to, limit ?? 100, cancellationToken));

app.MapPlaybackApi();
app.MapReconciliationApi();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.Run();

public partial class Program;
