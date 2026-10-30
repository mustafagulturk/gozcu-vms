using Gozcu.Media.Probing;
using Gozcu.Media.Processes;
using Microsoft.Extensions.Options;

namespace Gozcu.RecordingService.Reconciliation;

public static class ReconciliationServiceCollectionExtensions
{
    /// <summary>
    /// Açılış uzlaştırmasını kaydeder. RecorderService'ten önce çağrılmalıdır: barındırılan servisler kayıt
    /// sırasıyla başlatılır ve uzlaştırma disk listesini kayıt süreçleri yeni dosya açmadan önce almalıdır.
    /// </summary>
    public static IServiceCollection AddStartupReconciliation(this IServiceCollection services)
    {
        services.AddSingleton<ReconciliationReport>();
        services.AddSingleton<IMediaProbe>(sp => new FfprobeMediaProbe(
            sp.GetRequiredService<IProcessRunner>(),
            sp.GetRequiredService<IOptions<RecordingOptions>>().Value.FfprobePath));
        services.AddHostedService<StartupReconciliationService>();
        return services;
    }

    public static IEndpointRouteBuilder MapReconciliationApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/recordings/reconciliation", (ReconciliationReport report) => report.Snapshot());
        return app;
    }
}
