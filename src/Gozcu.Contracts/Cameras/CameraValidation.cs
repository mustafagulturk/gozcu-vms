using System.Text.RegularExpressions;

namespace Gozcu.Contracts.Cameras;

/// <summary>Kamera alanlarının doğrulama kuralları. Yapılandırma (tohum) ve yönetim API'si aynı kuralları kullanır.</summary>
public static partial class CameraValidation
{
    public const int MaxNameLength = 200;

    public const int MaxRtspUrlLength = 2048;

    /// <summary>Kimlik hatalıysa nedenini, geçerliyse <c>null</c> döner.</summary>
    public static string? ValidateId(string? id)
    {
        if (string.IsNullOrEmpty(id) || !IdPattern().IsMatch(id))
        {
            return $"Geçersiz kamera kimliği '{id}'. Yalnızca harf, rakam, '-' ve '_' kullanılabilir (en fazla 64 karakter).";
        }

        // "_" ile başlayan dizinler kayıt kökünde sisteme ayrılmıştır (ör. _quarantine).
        return id.StartsWith('_') ? $"Kamera kimliği '{id}' '_' ile başlayamaz." : null;
    }

    /// <summary>Kamera adı hatalıysa nedenini, geçerliyse <c>null</c> döner.</summary>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Kamera adı boş olamaz.";
        }

        return name.Length > MaxNameLength ? $"Kamera adı en fazla {MaxNameLength} karakter olabilir." : null;
    }

    /// <summary>Adres <c>rtsp://</c> ya da <c>rtsps://</c> ile başlayan, sunucu adı olan mutlak bir adres olmalı.</summary>
    public static bool IsValidRtspUrl(Uri? url) =>
        url is { IsAbsoluteUri: true, Scheme: "rtsp" or "rtsps" } && !string.IsNullOrEmpty(url.Host);

    /// <summary>Metni RTSP adresi olarak ayrıştırır; hatalıysa <paramref name="error"/> doldurulur.</summary>
    public static bool TryParseRtspUrl(string? value, out Uri? url, out string? error)
    {
        url = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "RTSP adresi gerekli.";
            return false;
        }

        if (value.Length > MaxRtspUrlLength)
        {
            error = $"RTSP adresi en fazla {MaxRtspUrlLength} karakter olabilir.";
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed) || !IsValidRtspUrl(parsed))
        {
            error = "Geçerli bir rtsp:// ya da rtsps:// adresi gerekli (ör. rtsp://kullanici:parola@192.168.1.10:554/stream1).";
            return false;
        }

        url = parsed;
        return true;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdPattern();
}
