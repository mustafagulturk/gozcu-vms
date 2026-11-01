using Microsoft.AspNetCore.Diagnostics.HealthChecks;

// Web paneli ve API ağ geçidi: tarayıcı yalnızca bu servise konuşur. Sayfalar buradan sunulur, /api/** istekleri
// YARP ile sahibi olan servise iletilir. Böylece tek köken (origin) olur; CORS gerekmez.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapReverseProxy();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");

app.Run();

public partial class Program;
