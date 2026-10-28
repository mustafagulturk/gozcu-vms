namespace Gozcu.Infrastructure.Persistence;

/// <summary>Şema hazır olduğunda tamamlanan sinyal; veritabanı yazıcıları bunu bekler.</summary>
public sealed class DatabaseReadiness
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsReady => _ready.Task.IsCompleted;

    public Task WaitAsync(CancellationToken cancellationToken) => _ready.Task.WaitAsync(cancellationToken);

    internal void MarkReady() => _ready.TrySetResult();
}
