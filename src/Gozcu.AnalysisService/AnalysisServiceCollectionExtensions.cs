using Gozcu.AnalysisService.Persistence;
using Gozcu.AnalysisService.Pipeline;
using Gozcu.Media.Frames;
using Gozcu.Vision;
using Microsoft.Extensions.Options;

namespace Gozcu.AnalysisService;

public static class AnalysisServiceCollectionExtensions
{
    /// <summary>
    /// Analiz hattını kaydeder. Tespit edici dışarıdan verilen fabrikayla oluşturulur; böylece servis gerçek modelden
    /// (ONNX) bağımsız kalır ve testler sahte tespit edici kullanabilir. Fabrika ilk kullanımda bir kez çağrılır;
    /// istisna fırlatırsa analiz başlamaz ve "detector" hazır olma kontrolü başarısız olur.
    /// </summary>
    public static IServiceCollection AddAnalysis(
        this IServiceCollection services,
        IConfiguration configuration,
        Func<IServiceProvider, IObjectDetector> detectorFactory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(detectorFactory);

        services.Configure<AnalysisOptions>(configuration.GetSection(AnalysisOptions.SectionName));
        services.AddSingleton(detectorFactory);
        services.AddSingleton<DetectorProvider>();
        services.AddSingleton<IFrameSource, FfmpegFrameSource>();
        services.AddSingleton(sp => new InferenceGate(sp.GetRequiredService<IOptions<AnalysisOptions>>().Value.MaxConcurrentInferences));
        services.AddSingleton(_ => new DetectionQueue());
        services.AddSingleton<AnalyzerRegistry>();
        services.AddSingleton<DetectionWriterMetrics>();

        // Barındırılan servisler kayıt sırasının tersine durdurulur: yazıcı analizcilerden önce kaydedilir ki kapanışta
        // önce analizciler durup açık olayları kapatsın, sonra yazıcı kalanları veritabanına yazsın.
        services.AddHostedService<DetectionWriter>();
        services.AddHostedService<AnalyzerService>();
        services.AddHostedService<DetectionRetentionService>();
        services.AddHealthChecks().AddCheck<DetectorHealthCheck>("detector", tags: ["ready"]);
        return services;
    }
}
