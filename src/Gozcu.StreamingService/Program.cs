using System.Text.Json.Serialization;
using Gozcu.Contracts.Cameras;
using Gozcu.Infrastructure;
using Gozcu.Media.Processes;
using Gozcu.StreamingService;
using Gozcu.StreamingService.Health;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<CameraOptions>(o => builder.Configuration.GetSection(CameraOptions.SectionName).Bind(o.Items));
builder.Services.Configure<StreamingOptions>(builder.Configuration.GetSection(StreamingOptions.SectionName));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<CameraRegistry>();
builder.Services.AddGozcuPersistence(builder.Configuration);
builder.Services.AddGozcuMessaging(builder.Configuration);
builder.Services.AddHostedService<CameraMonitorService>();
builder.Services.AddHostedService<CameraCatalogSyncService>();

var app = builder.Build();

var cameraErrors = app.Services.GetRequiredService<IOptions<CameraOptions>>().Value.Validate();
if (cameraErrors.Count > 0)
{
    throw new InvalidOperationException("Kamera yapılandırması hatalı:\n" + string.Join('\n', cameraErrors));
}

app.UseDefaultFiles();
app.UseStaticFiles();

var cameras = app.MapGroup("/api/cameras");
cameras.MapGet("/", (CameraRegistry registry) => registry.All.Select(t => t.Snapshot()));
cameras.MapGet("/{id}", (string id, CameraRegistry registry) =>
    registry.Find(id) is { } tracker ? Results.Ok(tracker.Snapshot()) : Results.NotFound());

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.Run();

public partial class Program;
