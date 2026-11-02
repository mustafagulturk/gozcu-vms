using System.Text.RegularExpressions;

namespace Gozcu.Contracts.Cameras;

/// <summary>
/// RTSP adreslerindeki parolayı gizler: <c>rtsp://admin:gizli@host/yol</c> → <c>rtsp://admin:***@host/yol</c>.
/// API yanıtları ve günlükler parolayı hiçbir zaman açık olarak içermez.
/// </summary>
public static partial class RtspUrlMasking
{
    public const string Mask = "***";

    public static string? MaskPassword(Uri? url) => url is null ? null : MaskPassword(url.OriginalString);

    public static string MaskPassword(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return url;
        }

        var authorityStart = schemeEnd + 3;
        var authorityEnd = url.IndexOfAny(['/', '?', '#'], authorityStart);
        if (authorityEnd < 0)
        {
            authorityEnd = url.Length;
        }

        if (authorityEnd == authorityStart)
        {
            return url;
        }

        // Kullanıcı bilgisini sunucudan ayıran, yetki bölümündeki son '@'.
        var at = url.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart);
        if (at < 0)
        {
            return url;
        }

        var colon = url.IndexOf(':', authorityStart, at - authorityStart);
        if (colon < 0)
        {
            // Yalnızca kullanıcı adı var; gizlenecek parola yok.
            return url;
        }

        return string.Concat(url.AsSpan(0, colon + 1), Mask, url.AsSpan(at));
    }

    /// <summary>
    /// Serbest metindeki (ör. ffmpeg hata satırı) tüm <c>şema://kullanıcı:parola@</c> parolalarını gizler.
    /// ffmpeg bağlantı hatalarında adresi olduğu gibi yazar; bu satırlar durum API'lerinde gösterilir.
    /// </summary>
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Contains("://", StringComparison.Ordinal) ? CredentialsPattern().Replace(text, "$1" + Mask + "@") : text;
    }

    /// <summary>Adreste gizlenmiş bir parola (<c>:***@</c>) var mı?</summary>
    public static bool IsMasked(string? url) =>
        url is not null && url.Contains(":" + Mask + "@", StringComparison.Ordinal);

    /// <summary>
    /// Gizlenmiş parolalı adresteki <c>***</c> yerine <paramref name="original"/> adresindeki gerçek parolayı koyar.
    /// Böylece panel parolayı yeniden yazdırmadan yalnızca sunucuyu ya da yolu değiştirebilir. Adres gizlenmiş
    /// değilse olduğu gibi, özgün adreste parola yoksa <c>null</c> döner.
    /// </summary>
    public static string? Unmask(string url, string original)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(original);
        if (!IsMasked(url))
        {
            return url;
        }

        var match = CredentialsPattern().Match(original);
        if (!match.Success)
        {
            return null;
        }

        // Eşleşme "şema://kullanıcı:" + parola + "@"; parola grup dışında kalan kısımdır.
        var password = match.Value[match.Groups[1].Length..^1];
        return url.Replace(":" + Mask + "@", ":" + password + "@", StringComparison.Ordinal);
    }

    // şema://kullanıcı:parola@ — kullanıcı adı ':' ve '@' içermez, parola boşluk, '/' ve '@' içermez.
    [GeneratedRegex(@"([A-Za-z][A-Za-z0-9+.-]*://[^\s:/@]*:)[^\s/@]+@")]
    private static partial Regex CredentialsPattern();
}
