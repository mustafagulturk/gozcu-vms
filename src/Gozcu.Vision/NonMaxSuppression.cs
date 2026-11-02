namespace Gozcu.Vision;

/// <summary>
/// Sınıf bazlı açgözlü NMS (non-maximum suppression): aynı nesne için üretilmiş, birbiriyle çok örtüşen kutulardan
/// yalnızca en güvenlisini bırakır. Farklı sınıflar (ör. otomobil ve kamyon) birbirini bastırmaz.
/// </summary>
public static class NonMaxSuppression
{
    /// <param name="candidates">Aday tespitler (sıra önemsiz).</param>
    /// <param name="iouThreshold">Aynı sınıftan daha güvenli bir kutuyla IoU'su bu değerden büyük olan aday atılır.</param>
    /// <param name="maxDetections">Döndürülecek en fazla tespit sayısı.</param>
    /// <returns>Güvene göre azalan sıralı tespitler.</returns>
    public static IReadOnlyList<Detection> Apply(IEnumerable<Detection> candidates, float iouThreshold, int maxDetections)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDetections);

        var kept = new List<Detection>();
        foreach (var candidate in candidates.OrderByDescending(d => d.Confidence))
        {
            if (kept.Count >= maxDetections)
            {
                break;
            }

            var suppressed = false;
            foreach (var existing in kept)
            {
                if (existing.Label == candidate.Label && existing.Box.IntersectionOverUnion(candidate.Box) > iouThreshold)
                {
                    suppressed = true;
                    break;
                }
            }

            if (!suppressed)
            {
                kept.Add(candidate);
            }
        }

        return kept;
    }
}
