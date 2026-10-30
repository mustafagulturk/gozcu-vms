namespace Gozcu.RecordingService.Reconciliation;

public enum ReconciliationState
{
    /// <summary>Servis henüz başlamadı.</summary>
    Pending,

    /// <summary>Disk listesi alındı; veritabanının hazır olması bekleniyor.</summary>
    WaitingForDatabase,

    Running,
    Completed,
    Failed,
}

/// <summary>Açılış uzlaştırmasının sonucu; GET /api/recordings/reconciliation ile okunur.</summary>
public sealed record ReconciliationStatus
{
    public ReconciliationState State { get; init; } = ReconciliationState.Pending;

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Açılışta diskte bulunan parça sayısı.</summary>
    public int Scanned { get; init; }

    /// <summary>Veritabanında aynı boyutla zaten kayıtlı olan (incelenmeyen) parçalar.</summary>
    public int AlreadyIndexed { get; init; }

    /// <summary>ffprobe ile incelenen parçalar.</summary>
    public int Probed { get; init; }

    /// <summary>Geçerli parçalar: zaten kayıtlı olanlar ve incelemede oynatılabilir çıkanlar.</summary>
    public int Valid { get; init; }

    /// <summary>Veritabanına eklenen (ya da boyutu düzeltilen) parçalar.</summary>
    public int Indexed { get; init; }

    /// <summary>Bozuk olduğu için karantina dizinine taşınan parçalar.</summary>
    public int Quarantined { get; init; }

    /// <summary>İçeriği olmayan (çok küçük) olduğu için silinen parçalar.</summary>
    public int RemovedTiny { get; init; }

    /// <summary>Dosyası diskte olmadığı için veritabanında silindi olarak işaretlenen satırlar.</summary>
    public int MarkedDeleted { get; init; }

    /// <summary>İncelenemeyen ya da bu arada değişen, dokunulmadan bırakılan parçalar.</summary>
    public int Skipped { get; init; }

    public double? SnapshotMs { get; init; }

    public double? DatabaseWaitMs { get; init; }

    public double? ProbeMs { get; init; }

    public double? TotalMs { get; init; }

    public string? Error { get; init; }
}

/// <summary>Uzlaştırma raporunu tutan tekil nesne; servis yazar, API okur.</summary>
public sealed class ReconciliationReport
{
    private readonly object _lock = new();
    private ReconciliationStatus _status = new();

    public ReconciliationStatus Snapshot()
    {
        lock (_lock)
        {
            return _status;
        }
    }

    public void Update(Func<ReconciliationStatus, ReconciliationStatus> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_lock)
        {
            _status = change(_status);
        }
    }
}
