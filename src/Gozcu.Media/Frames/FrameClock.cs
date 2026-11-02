using System.Globalization;
using System.Text.RegularExpressions;

namespace Gozcu.Media.Frames;

/// <summary>
/// Analiz karelerine "kameradan geliş" saatini verir.
/// <para>
/// Karenin stdout'tan okunduğu an, karenin sunucuya geldiği andan geç kalır: ffmpeg'in kod çözücüsü B kareler için
/// yeniden sıralama yapar, kareleri tamponlar, seyreltir ve ölçekler (ölçümde 0,5–1 sn). Kayıt servisi ise parçanın
/// başlangıcını karenin geliş anından hesaplar. İkisi farklı saatlerle damgalanırsa geri oynatmadaki tespit kutuları
/// görüntünün gerisinde kalır.
/// </para>
/// <para>
/// Çözüm: aynı ffmpeg süreci akışı ayrıca yeniden kodlamadan <c>framecrc</c> biçiminde stderr'e yazar. Her anahtar
/// kare paketi geldiği anda bir satır üretir; "geliş anı − PTS" akış zamanını duvar saatine bağlayan tabanı verir
/// (yalnızca anahtar kareler: diğer paketlerin PTS'i yeniden sıralama yüzünden geliş sırasından ileridedir). Analiz
/// zincirindeki <c>showinfo</c> süzgeci her çıkan karenin PTS'ini yazar. Kare zamanı = taban + PTS.
/// </para>
/// Sınıf iş parçacığı güvenlidir: satırlar stderr okuyucusundan, kare istekleri stdout okuyucusundan gelir.
/// </summary>
public sealed partial class FrameClock
{
    /// <summary>Tabanı hesaplarken bakılan son anahtar kare sayısı (en küçük gecikmeli olan seçilir).</summary>
    private const int BaseWindow = 5;

    /// <summary>Kare çıktısından önce gelmemiş PTS satırı için en fazla bu kadar beklenir.</summary>
    public static readonly TimeSpan PtsWait = TimeSpan.FromMilliseconds(250);

    /// <summary>Hesaplanan zaman okuma anından bu kadar farklıysa güvenilmez sayılır ve okuma anı kullanılır.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);

    private readonly object _lock = new();
    private readonly Queue<TimeSpan> _pending = new();
    private readonly Queue<DateTimeOffset> _bases = new();
    private double _timeBase = 1.0 / 90_000;
    private int _skipPts;
    private DateTimeOffset _lastCaptured = DateTimeOffset.MinValue;

    /// <summary>Son karenin okuma anı ile geliş anı arasındaki fark (kod çözme hattı gecikmesi).</summary>
    public TimeSpan? LastDelay { get; private set; }

    /// <summary>
    /// stderr satırını işler. Satır framecrc ya da showinfo satırıysa <c>true</c> döner; çağıran bu satırları
    /// tanı/hata satırı olarak değerlendirmemelidir.
    /// </summary>
    public bool TryConsume(string line, DateTimeOffset receivedAt)
    {
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        if (line.StartsWith("#tb 0:", StringComparison.Ordinal))
        {
            var match = TimeBasePattern().Match(line);
            if (match.Success)
            {
                var num = double.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
                var den = double.Parse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture);
                if (num > 0 && den > 0)
                {
                    lock (_lock)
                    {
                        _timeBase = num / den;
                    }
                }
            }

            return true;
        }

        if (line.StartsWith('#'))
        {
            // framecrc başlığının diğer satırları (#software, #codec_id ...). ffmpeg günlük satırları '#' ile başlamaz.
            return true;
        }

        if (line.StartsWith("0,", StringComparison.Ordinal))
        {
            var match = PacketPattern().Match(line);
            if (!match.Success)
            {
                return false;
            }

            // Anahtar olmayan paketlerde "F=0x0" bayrağı yazılır; anahtar karelerde bayrak alanı yoktur.
            if (!line.Contains("F=", StringComparison.Ordinal))
            {
                var pts = long.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
                lock (_lock)
                {
                    _bases.Enqueue(receivedAt - TimeSpan.FromSeconds(pts * _timeBase));
                    while (_bases.Count > BaseWindow)
                    {
                        _bases.Dequeue();
                    }
                }
            }

            return true;
        }

        if (line.Contains("Parsed_showinfo", StringComparison.Ordinal))
        {
            var match = ShowInfoPattern().Match(line);
            if (match.Success)
            {
                var pts = TimeSpan.FromSeconds(double.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture));
                lock (_lock)
                {
                    if (_skipPts > 0)
                    {
                        // Bu PTS'in karesi beklenmeden işlendi (bkz. CaptureTimeAsync); eşleşme kaymasın diye atılır.
                        _skipPts--;
                    }
                    else
                    {
                        _pending.Enqueue(pts);
                    }
                }
            }

            // showinfo karenin düzlemleri, renk bilgisi gibi ek satırlar da yazar; hepsi tanı dışıdır.
            return true;
        }

        return false;
    }

    /// <summary>
    /// stdout'tan yeni okunan karenin geliş zamanı. PTS satırı henüz işlenmediyse kısa süre bekler; hesaplanamazsa
    /// okuma anını döner.
    /// </summary>
    public async Task<DateTimeOffset> CaptureTimeAsync(DateTimeOffset readAt, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(time);
        var deadline = time.GetUtcNow() + PtsWait;
        while (true)
        {
            lock (_lock)
            {
                if (_pending.TryDequeue(out var pts))
                {
                    return Monotonic(Resolve(readAt, pts));
                }

                if (time.GetUtcNow() >= deadline)
                {
                    _skipPts++;
                    LastDelay = null;
                    return Monotonic(readAt);
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Kare zamanları geri gitmemeli: olay takibi ve veritabanı sırası buna dayanır. Açılışta ffmpeg paketleri çıkışlar
    /// hazır olana kadar tamponlayıp topluca yazdığı için ilk taban geç kalabilir; sonraki anahtar karede düzelince
    /// zamanlar geriye sıçramasın diye kısa süre son zamanın hemen sonrasına sabitlenir.
    /// </summary>
    private DateTimeOffset Monotonic(DateTimeOffset value)
    {
        if (value <= _lastCaptured)
        {
            value = _lastCaptured + TimeSpan.FromMilliseconds(1);
        }

        _lastCaptured = value;
        return value;
    }

    private DateTimeOffset Resolve(DateTimeOffset readAt, TimeSpan pts)
    {
        if (_bases.Count == 0)
        {
            LastDelay = null;
            return readAt;
        }

        // Geliş anı ≥ PTS'in gerçek geliş zamanı; ağ ve okuma gecikmesi hep pozitif olduğundan en küçük taban en iyisidir.
        var captured = _bases.Min() + pts;
        var delay = readAt - captured;
        if (delay < TimeSpan.Zero || delay > MaxDelay)
        {
            LastDelay = null;
            return readAt;
        }

        LastDelay = delay;
        return captured;
    }

    [GeneratedRegex(@"^#tb 0: (\d+)/(\d+)")]
    private static partial Regex TimeBasePattern();

    [GeneratedRegex(@"^0,\s*-?\d+,\s*(-?\d+),")]
    private static partial Regex PacketPattern();

    [GeneratedRegex(@"pts_time:(-?[\d.]+)")]
    private static partial Regex ShowInfoPattern();
}
