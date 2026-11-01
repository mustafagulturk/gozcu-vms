using System.Text.Json.Serialization;
using Gozcu.Contracts.Cameras;
using Gozcu.Infrastructure;
using Gozcu.Media.Probing;
using Gozcu.Media.Processes;
using Gozcu.StreamingService;
using Gozcu.StreamingService.Cameras;
using Gozcu.StreamingService.Health;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// "Cameras" yalnızca tohumdur: kamera tablosu boşsa ilk açılışta yazılır, sonra yönetim API'si kullanılır.
builder.Services.Configure<CameraOptions>(o => builder.Configuration.GetSection(CameraOptions.SectionName).Bind(o.Items));
builder.Services.Configure<StreamingOptions>(builder.Configuration.GetSection(StreamingOptions.SectionName));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<IStreamProbe>(sp =>
{
    var options = sp.GetRequiredService<IOptions<StreamingOptions>>().Value;
    return new FfprobeStreamProbe(sp.GetRequiredService<IProcessRunner>(), options.FfprobePath, options.ProbeTimeout);
});
builder.Services.AddSingleton<CameraRegistry>();
builder.Services.AddGozcuPersistence(builder.Configuration);
builder.Services.AddGozcuMessaging(builder.Configuration);
builder.Services.AddGozcuCameraCatalog(builder.Configuration);
builder.Services.AddHostedService<CameraSeedService>();
builder.Services.AddHostedService<CameraMonitorService>();

var app = builder.Build();

var cameraErrors = app.Services.GetRequiredService<IOptions<CameraOptions>>().Value.Validate();
if (cameraErrors.Count > 0)
{
    throw new InvalidOperationException("Kamera yapılandırması (tohum) hatalı:\n" + string.Join('\n', cameraErrors));
}

app.MapCameraManagementApi();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.Run();

public partial class Program;
