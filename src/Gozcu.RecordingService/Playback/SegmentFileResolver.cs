namespace Gozcu.RecordingService.Playback;

/// <summary>
/// Veritabanındaki parça yolunu kayıt kök dizini içinde kalacak şekilde doğrular.
/// Kök dışına çıkan (ör. "../") ya da diskte bulunmayan yollar reddedilir; böylece veritabanındaki
/// bozuk ya da kötü niyetli bir kayıt rastgele dosya okutmak için kullanılamaz.
/// </summary>
public sealed class SegmentFileResolver
{
    private readonly string _root;

    public SegmentFileResolver(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)) + Path.DirectorySeparatorChar;
    }

    /// <summary>Yol kök içindeyse ve dosya varsa tam yolu, aksi halde <c>null</c> döner.</summary>
    public string? Resolve(string? filePath)
    {
        if (!IsInsideRoot(filePath, out var fullPath))
        {
            return null;
        }

        return File.Exists(fullPath) ? fullPath : null;
    }

    /// <summary>Yalnızca yolun kök içinde kalıp kalmadığını denetler (dosya sistemine bakmaz).</summary>
    public bool IsInsideRoot(string? filePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(filePath) || filePath.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            // Göreli yollar, RootPath gibi çalışma dizinine göre çözülür.
            fullPath = Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullPath.StartsWith(_root, comparison) && fullPath.Length > _root.Length;
    }
}
