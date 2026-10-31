using System.Text.Json.Serialization;
using Gozcu.AnalysisService;
using Gozcu.AnalysisService.Api;
using Gozcu.Contracts.Cameras;
using Gozcu.Infrastructure;
using Gozcu.Vision;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<CameraOptions>(o => builder.Configuration.GetSection(CameraOptions.SectionName).Bind(o.Items));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddGozcuPersistence(builder.Configuration);
builder.Services.AddGozcuMessaging(builder.Configuration);
builder.Services.AddAnalysis(builder.Configuration, services =>
{
    var options = services.GetRequiredService<IOptions<AnalysisOptions>>().Value;
    return new OnnxYoloxDetector(
        new YoloxDetectorOptions
        {
            ModelPath = options.ModelPath,
            ConfidenceThreshold = options.ConfidenceThreshold,
            NmsThreshold = options.NmsThreshold,
            MaxDetections = options.MaxDetections,
            IntraOpThreads = options.IntraOpThreads,
        },
        services.GetRequiredService<ILogger<OnnxYoloxDetector>>());
});

var app = builder.Build();

var cameraErrors = app.Services.GetRequiredService<IOptions<CameraOptions>>().Value.Validate();
if (cameraErrors.Count > 0)
{
    throw new InvalidOperationException("Kamera yapılandırması hatalı:\n" + string.Join('\n', cameraErrors));
}

var analysisErrors = app.Services.GetRequiredService<IOptions<AnalysisOptions>>().Value.Validate();
if (analysisErrors.Count > 0)
{
    throw new InvalidOperationException("Analiz yapılandırması hatalı:\n" + string.Join('\n', analysisErrors));
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapAnalysisApi();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.Run();

public partial class Program;
