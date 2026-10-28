using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gozcu.Infrastructure.Persistence;

/// <summary>
/// Veritabanı erişilebilir olana kadar arka planda şemayı uygulamayı dener.
/// Servisin geri kalanı (ör. diske kayıt) veritabanını beklemeden çalışmaya başlar.
/// </summary>
public sealed class DatabaseMigrationService : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private readonly DatabaseMigrator _migrator;
    private readonly DatabaseReadiness _readiness;
    private readonly ILogger<DatabaseMigrationService> _logger;

    public DatabaseMigrationService(DatabaseMigrator migrator, DatabaseReadiness readiness, ILogger<DatabaseMigrationService> logger)
    {
        _migrator = migrator;
        _readiness = readiness;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _migrator.MigrateAsync(stoppingToken).ConfigureAwait(false);
                _readiness.MarkReady();
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Veritabanına ulaşılamadı ({Message}); {Delay} sonra tekrar denenecek", ex.Message, RetryDelay);
                await Task.Delay(RetryDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
