namespace Gozcu.Benchmarks.Support;

/// <summary>
/// Ölçümlerin ihtiyaç duyduğu depo dosyalarını (model, test görüntüsü) bulur. BenchmarkDotNet her ölçümü
/// <c>bin/Release/...</c> altında üretilmiş ayrı bir süreçte çalıştırdığı için göreli yollar işe yaramaz;
/// dosya, <see cref="AppContext.BaseDirectory"/>'den üst klasörlere doğru yürünerek aranır.
/// </summary>
internal static class RepoFiles
{
    /// <summary>Varsayılan model dosyasının depo köküne göre yolu.</summary>
    public const string DefaultModel = "models/yolox_tiny.onnx";

    /// <summary>Model yolunu veren ortam değişkeni (entegrasyon testleriyle aynı ad).</summary>
    public const string ModelPathVariable = "GOZCU_MODEL_PATH";

    /// <summary>
    /// <paramref name="relativePath"/>'i çalışma klasöründen başlayıp üst klasörlere doğru arar; ilk bulunan tam yolu,
    /// hiçbir yerde yoksa <c>null</c> döner. Çalışma ağaçları (worktree) ana deponun altında durduğundan ana
    /// kopyadaki dosya da bulunur.
    /// </summary>
    public static string? FindUpwards(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// YOLOX model dosyasının yolu: önce <c>GOZCU_MODEL_PATH</c>, yoksa üst klasörlerde <c>models/yolox_tiny.onnx</c> araması.
    /// </summary>
    /// <exception cref="FileNotFoundException">Model hiçbir yerde yoksa; mesaj modelin nasıl indirileceğini söyler.</exception>
    public static string ResolveModelPath()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(ModelPathVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            var fullPath = Path.GetFullPath(fromEnvironment);
            return File.Exists(fullPath)
                ? fullPath
                : throw new FileNotFoundException($"{ModelPathVariable} ile verilen model dosyası bulunamadı: {fullPath}", fullPath);
        }

        return FindUpwards(DefaultModel)
            ?? throw new FileNotFoundException(
                $"Model dosyası ({DefaultModel}) {AppContext.BaseDirectory} ve üst klasörlerinde bulunamadı. " +
                $"Önce ./scripts/download-model.sh çalıştırın veya {ModelPathVariable} ile model yolunu verin.");
    }
}
