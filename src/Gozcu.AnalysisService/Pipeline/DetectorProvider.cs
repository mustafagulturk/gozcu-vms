using Gozcu.Vision;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Gozcu.AnalysisService.Pipeline;

/// <summary>
/// Tespit ediciyi (ve modeli) ilk kullanımda bir kez yükler. Model yüklenemezse servis çökmez: hata saklanır,
/// "detector" hazır olma kontrolü başarısız olur ve analiz başlamaz.
/// </summary>
public sealed class DetectorProvider
{
    private readonly Lazy<(IObjectDetector? Detector, Exception? Error)> _detector;

    public DetectorProvider(IServiceProvider services)
    {
        _detector = new Lazy<(IObjectDetector?, Exception?)>(() =>
        {
            try
            {
                return (services.GetRequiredService<IObjectDetector>(), null);
            }
            catch (Exception ex)
            {
                return (null, ex);
            }
        });
    }

    public Exception? Error => _detector.Value.Error;

    /// <summary>Yüklenmişse modelin bilgisi, aksi halde null.</summary>
    public DetectorModelInfo? Model => _detector.Value.Detector?.Model;

    public bool TryGet(out IObjectDetector detector)
    {
        detector = _detector.Value.Detector!;
        return detector is not null;
    }
}

public sealed class DetectorHealthCheck : IHealthCheck
{
    private readonly DetectorProvider _provider;

    public DetectorHealthCheck(DetectorProvider provider) => _provider = provider;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(_provider.TryGet(out var detector)
            ? HealthCheckResult.Healthy($"Model yüklü: {detector.Model.Name} ({detector.Model.InputWidth}x{detector.Model.InputHeight})")
            : HealthCheckResult.Unhealthy("Nesne tespit modeli yüklenemedi.", _provider.Error));
}
