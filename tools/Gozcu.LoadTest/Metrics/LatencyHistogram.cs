using System.Numerics;

namespace Gozcu.LoadTest.Metrics;

/// <summary>
/// HdrHistogram benzeri log-doğrusal gecikme histogramı (mikrosaniye cinsinden).
/// <para>
/// 0..127 µs her biri tek değerlik kovalarda tutulur. Daha büyük değerler için her ikinin kuvveti aralığı
/// ([2^k, 2^(k+1))) 64 eşit kovaya bölünür; böylece göreli hata en fazla 1/64 (~%1,6) olur ve bellek sabittir
/// (~2 200 kova, ~18 KB). Kayıt ayırma yapmaz ve kilitsizdir: sayaçlar <see cref="Interlocked"/> ile artırılır,
/// bu yüzden çok sayıda işçi aynı histograma güvenle yazabilir.
/// </para>
/// </summary>
internal sealed class LatencyHistogram
{
    public const int SubBucketBits = 7;
    public const int SubBucketCount = 1 << SubBucketBits; // 128
    public const int HalfSubBucketCount = SubBucketCount / 2; // 64

    /// <summary>İzlenebilen en büyük değer (~12,7 gün); üstü bu değere kırpılır.</summary>
    public const long MaxTrackableValue = 1L << 40;

    public static readonly int BucketCount = IndexOf(MaxTrackableValue) + 1;

    private readonly long[] _counts = new long[BucketCount];
    private long _totalCount;
    private long _sum;
    private long _min = long.MaxValue;
    private long _max;

    public long TotalCount => Interlocked.Read(ref _totalCount);

    public long Min => TotalCount == 0 ? 0 : Interlocked.Read(ref _min);

    public long Max => Interlocked.Read(ref _max);

    public double Mean => TotalCount == 0 ? 0 : (double)Interlocked.Read(ref _sum) / TotalCount;

    /// <summary>Değerin düştüğü kova.</summary>
    public static int IndexOf(long value)
    {
        value = Math.Clamp(value, 0, MaxTrackableValue);
        if (value < SubBucketCount)
        {
            return (int)value;
        }

        var msb = 63 - BitOperations.LeadingZeroCount((ulong)value);
        var shift = msb - (SubBucketBits - 1); // >= 1
        var mantissa = (int)(value >> shift); // 64..127
        return SubBucketCount + ((shift - 1) * HalfSubBucketCount) + (mantissa - HalfSubBucketCount);
    }

    /// <summary>Kovaya düşen en küçük değer.</summary>
    public static long LowestValueAt(int index)
    {
        if (index < SubBucketCount)
        {
            return index;
        }

        var relative = index - SubBucketCount;
        var shift = (relative / HalfSubBucketCount) + 1;
        var mantissa = (relative % HalfSubBucketCount) + HalfSubBucketCount;
        return (long)mantissa << shift;
    }

    /// <summary>Kovaya düşen en büyük değer.</summary>
    public static long HighestValueAt(int index)
    {
        if (index < SubBucketCount)
        {
            return index;
        }

        var shift = ((index - SubBucketCount) / HalfSubBucketCount) + 1;
        return LowestValueAt(index) + (1L << shift) - 1;
    }

    public void Record(long value)
    {
        value = Math.Clamp(value, 0, MaxTrackableValue);
        Interlocked.Increment(ref _counts[IndexOf(value)]);
        Interlocked.Increment(ref _totalCount);
        Interlocked.Add(ref _sum, value);
        UpdateMin(value);
        UpdateMax(value);
    }

    /// <summary>
    /// Yüzdelik değer (0..100). Sıralamada ⌈p/100·n⌉. öğenin kovasının üst sınırı döner (HdrHistogram'daki
    /// "highest equivalent value"); sonuç gözlenen en küçük/en büyük değerle sınırlanır, böylece p100 tam en büyük değerdir.
    /// </summary>
    public long Percentile(double percentile)
    {
        var total = TotalCount;
        if (total == 0)
        {
            return 0;
        }

        percentile = Math.Clamp(percentile, 0, 100);
        var rank = Math.Max(1, (long)Math.Ceiling(percentile / 100.0 * total));
        long cumulative = 0;
        for (var i = 0; i < _counts.Length; i++)
        {
            cumulative += Interlocked.Read(ref _counts[i]);
            if (cumulative >= rank)
            {
                return Math.Clamp(HighestValueAt(i), Min, Max);
            }
        }

        return Max;
    }

    /// <summary>Başka bir histogramı bu histograma ekler (ör. uç noktaları toplam satırında birleştirmek için).</summary>
    public void Add(LatencyHistogram other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.TotalCount == 0)
        {
            return;
        }

        for (var i = 0; i < _counts.Length; i++)
        {
            var c = Interlocked.Read(ref other._counts[i]);
            if (c != 0)
            {
                Interlocked.Add(ref _counts[i], c);
            }
        }

        Interlocked.Add(ref _totalCount, other.TotalCount);
        Interlocked.Add(ref _sum, Interlocked.Read(ref other._sum));
        UpdateMin(other.Min);
        UpdateMax(other.Max);
    }

    private void UpdateMin(long value)
    {
        var current = Interlocked.Read(ref _min);
        while (value < current)
        {
            var previous = Interlocked.CompareExchange(ref _min, value, current);
            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }

    private void UpdateMax(long value)
    {
        var current = Interlocked.Read(ref _max);
        while (value > current)
        {
            var previous = Interlocked.CompareExchange(ref _max, value, current);
            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }
}
