using System.Globalization;
using Gozcu.Contracts.Cameras;
using Gozcu.Infrastructure.Persistence;
using Gozcu.StreamingService.Health;

namespace Gozcu.StreamingService.Cameras;

/// <summary>Yönetim API'sinin kamera görünümü. Parola hiçbir zaman açık döndürülmez.</summary>
/// <param name="Health">Anlık sağlık durumu; devre dışı ya da henüz izlenmeye başlamamış kamerada null.</param>
public sealed record CameraView(
    string Id,
    string Name,
    string RtspUrl,
    bool Enabled,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    CameraHealthSnapshot? Health)
{
    public static CameraView From(CameraRecord camera, CameraRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(registry);
        var health = camera.Enabled ? registry.Find(camera.Id)?.Snapshot() : null;
        return new CameraView(
            camera.Id, camera.Name, RtspUrlMasking.MaskPassword(camera.RtspUrl), camera.Enabled, camera.Version,
            camera.CreatedAt, camera.UpdatedAt, health);
    }
}

public sealed record CreateCameraRequest(string? Id, string? Name, string? RtspUrl, bool? Enabled);

/// <param name="RtspUrl">Boş ya da gizlenmiş haliyle aynıysa mevcut adres korunur.</param>
/// <param name="Enabled">Verilmezse mevcut değer korunur.</param>
public sealed record UpdateCameraRequest(string? Name, string? RtspUrl, bool? Enabled);

/// <param name="CameraId">
/// Verilirse ve adres gizlenmiş parola (<c>***</c>) içeriyorsa kayıtlı parola kullanılır; böylece panel parolayı
/// yeniden yazdırmadan mevcut kamerayı test edebilir.
/// </param>
public sealed record ProbeCameraRequest(string? RtspUrl, string? CameraId);

/// <summary>Bağlantı testi yanıtı: başarılıysa <c>{ ok:true, codec, width, height, fps }</c>, değilse <c>{ ok:false, error }</c>.</summary>
public sealed record ProbeCameraResponse(bool Ok, string? Codec, int? Width, int? Height, double? Fps, string? Error);

/// <summary>HTTP varlık etiketi (ETag) ile kamera sürümü arasındaki dönüşüm.</summary>
public static class CameraEntityTag
{
    public static string Format(int version) => "\"" + version.ToString(CultureInfo.InvariantCulture) + "\"";

    /// <summary>
    /// <c>If-Match</c> başlığını okur. Başlık yoksa ya da <c>*</c> ise koşul yoktur (<paramref name="version"/> null).
    /// <c>"5"</c> ya da <c>W/"5"</c> biçimini kabul eder; okunamayan değerde false döner (412 ile yanıtlanır).
    /// </summary>
    public static bool TryParseIfMatch(string? header, out int? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(header))
        {
            return true;
        }

        var value = header.Trim();
        if (value == "*")
        {
            return true;
        }

        if (value.StartsWith("W/", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            version = parsed;
            return true;
        }

        return false;
    }
}
