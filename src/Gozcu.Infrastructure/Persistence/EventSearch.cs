using System.Globalization;
using System.Text;
using Gozcu.Contracts.Analysis;

namespace Gozcu.Infrastructure.Persistence;

/// <summary>
/// Olay araması ölçütleri. Boş <see cref="CameraIds"/> tüm kameralar demektir. Zaman aralığı kesişim olarak
/// uygulanır: aralıktan önce başlayıp içinde biten (ya da hâlâ süren) olaylar da bulunur.
/// </summary>
/// <param name="Now">Süren olayların süresi bu ana kadar hesaplanır (<see cref="MinDuration"/> için).</param>
/// <param name="After">Önceki sayfanın son olayı; verilirse ondan sonraki (daha eski) olaylar döner.</param>
public sealed record EventSearchQuery(
    IReadOnlyList<string> CameraIds,
    ObjectCategory? Category,
    DateTimeOffset? From,
    DateTimeOffset? To,
    float? MinConfidence,
    TimeSpan? MinDuration,
    DateTimeOffset Now,
    EventSearchCursor? After,
    int Limit);

/// <summary>Bir arama sayfası. <see cref="Next"/> null ise başka sonuç yoktur.</summary>
public sealed record EventSearchPage(IReadOnlyList<DetectionEventRecord> Items, EventSearchCursor? Next);

/// <summary>
/// Keyset sayfalama imleci: sayfanın son olayının (started_at, id) çifti. Sıralama started_at DESC, id DESC olduğundan
/// sonraki sayfa "(started_at, id) &lt; imleç" koşuluyla bulunur. Dışarıya
/// base64url("&lt;started_at unix mikro saniye&gt;|&lt;guid&gt;") olarak verilir; PostgreSQL timestamptz mikro saniye
/// çözünürlüklü olduğundan dönüşüm kayıpsızdır.
/// </summary>
public readonly record struct EventSearchCursor(DateTimeOffset StartedAt, Guid Id)
{
    // Bir mikro saniye 10 tick'tir.
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    private const int MaxEncodedLength = 128;

    public string Encode()
    {
        var micros = (StartedAt.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / TicksPerMicrosecond;
        var text = string.Create(CultureInfo.InvariantCulture, $"{micros}|{Id:D}");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>İmleci çözer; biçim bozuksa <c>false</c> döner (istisna fırlatmaz).</summary>
    public static bool TryDecode(string? value, out EventSearchCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxEncodedLength)
        {
            return false;
        }

        // base64url → base64: alfabe geri çevrilir, dolgu tamamlanır. '+', '/' ve '=' içeren girdi reddedilir.
        if (value.AsSpan().IndexOfAny("+/=") >= 0 || value.Length % 4 == 1)
        {
            return false;
        }

        var base64 = value.Replace('-', '+').Replace('_', '/').PadRight(value.Length + ((4 - (value.Length % 4)) % 4), '=');
        string text;
        try
        {
            text = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return false;
        }

        var separator = text.IndexOf('|', StringComparison.Ordinal);
        if (separator <= 0
            || !long.TryParse(text.AsSpan(0, separator), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var micros)
            || !Guid.TryParseExact(text.AsSpan(separator + 1), "D", out var id))
        {
            return false;
        }

        // 1970 öncesi ve DateTimeOffset.MaxValue sonrası anlamsızdır; taşmayı da önler.
        if (micros < 0 || micros > (DateTimeOffset.MaxValue.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / TicksPerMicrosecond)
        {
            return false;
        }

        var ticks = DateTimeOffset.UnixEpoch.UtcTicks + (micros * TicksPerMicrosecond);
        cursor = new EventSearchCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
        return true;
    }

    public override string ToString() => Encode();
}
