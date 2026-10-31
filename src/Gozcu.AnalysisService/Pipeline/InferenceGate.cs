namespace Gozcu.AnalysisService.Pipeline;

/// <summary>
/// Tüm kameralar arasında aynı anda çalışan çıkarım sayısını sınırlar (Analysis:MaxConcurrentInferences).
/// Çıkarım CPU'ya bağlı olduğundan iş parçacığı havuzunda çalıştırılır; kare okuyan görevler hiç bloklanmaz.
/// </summary>
public sealed class InferenceGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private int _active;

    public InferenceGate(int maxConcurrency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        MaxConcurrency = maxConcurrency;
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    public int MaxConcurrency { get; }

    /// <summary>Şu an çalışan çıkarım sayısı.</summary>
    public int Active => Volatile.Read(ref _active);

    /// <summary>
    /// Sıra gelince <paramref name="work"/>'ü iş parçacığı havuzunda çalıştırır. İptal yalnızca sıra beklerken etkilidir;
    /// başlamış bir çıkarım yarıda kesilmez.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        await _semaphore.WaitAsync(cancellationToken);
        Interlocked.Increment(ref _active);
        try
        {
            return await Task.Run(work, CancellationToken.None);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            _semaphore.Release();
        }
    }

    public void Dispose() => _semaphore.Dispose();
}
