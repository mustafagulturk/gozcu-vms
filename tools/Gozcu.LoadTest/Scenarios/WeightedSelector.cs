namespace Gozcu.LoadTest.Scenarios;

/// <summary>
/// Ağırlıklı rastgele seçim. Birikimli ağırlıklar üzerinde ikili arama yapar; seçim ayırma yapmaz ve
/// iş parçacığı güvenlidir (durum değişmez).
/// </summary>
internal sealed class WeightedSelector<T>
{
    private readonly T[] _items;
    private readonly double[] _cumulative;

    public WeightedSelector(IEnumerable<(T Item, double Weight)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var items = new List<T>();
        var cumulative = new List<double>();
        double sum = 0;
        foreach (var (item, weight) in entries)
        {
            if (double.IsNaN(weight) || weight < 0 || double.IsInfinity(weight))
            {
                throw new ArgumentException($"Ağırlık negatif olmayan sonlu bir sayı olmalı: {weight}.", nameof(entries));
            }

            if (weight == 0)
            {
                continue;
            }

            sum += weight;
            items.Add(item);
            cumulative.Add(sum);
        }

        if (items.Count == 0)
        {
            throw new ArgumentException("En az bir pozitif ağırlık gerekli.", nameof(entries));
        }

        _items = [.. items];
        _cumulative = [.. cumulative];
    }

    public double TotalWeight => _cumulative[^1];

    public IReadOnlyList<T> Items => _items;

    /// <summary>Öğenin seçilme olasılığı (0..1).</summary>
    public double Probability(int index) =>
        (_cumulative[index] - (index == 0 ? 0 : _cumulative[index - 1])) / TotalWeight;

    public T Next(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        return Select(random.NextDouble());
    }

    /// <summary><paramref name="roll"/> [0,1) aralığındaki bir sayı; testlerde belirlenimci seçim için.</summary>
    public T Select(double roll)
    {
        var target = Math.Clamp(roll, 0, 1) * TotalWeight;
        // İlk "birikimli > hedef" konumu: [0,w1) -> 0, [w1,w1+w2) -> 1 ...
        int lo = 0, hi = _cumulative.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_cumulative[mid] > target)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return _items[lo];
    }
}
