using Yarp.ReverseProxy.Transforms;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

// Web paneli ve API ağ geçidi: tarayıcı yalnızca bu servise konuşur. Sayfalar buradan sunulur, /api/** istekleri
// YARP ile sahibi olan servise iletilir. Böylece tek köken (origin) olur; CORS gerekmez.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(context =>
    {
        // MediaMTX ilk HLS isteğine kök göreli bir yönlendirmeyle ("/cam1/index.m3u8?cookieCheck=1") yanıt verir.
        // Ön ek yolda kaldırıldığı için yanıttaki Location başlığına geri eklenmeli, yoksa tarayıcı ağ geçidinin
        // köküne gider ve 404 alır.
        if (context.Route.Transforms?.Any(t => t.TryGetValue("PathRemovePrefix", out _)) != true)
        {
            return;
        }

        var prefix = context.Route.Transforms.First(t => t.ContainsKey("PathRemovePrefix"))["PathRemovePrefix"].TrimEnd('/');
        context.AddResponseTransform(transform =>
        {
            var headers = transform.HttpContext.Response.Headers;
            var location = headers.Location.ToString();
            if (location.StartsWith('/') && !location.StartsWith("//", StringComparison.Ordinal)
                && !location.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                headers.Location = prefix + location;
            }

            return ValueTask.CompletedTask;
        });
    });
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapReverseProxy();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");

app.Run();

public partial class Program;
