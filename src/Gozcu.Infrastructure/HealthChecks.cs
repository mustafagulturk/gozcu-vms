using Gozcu.Infrastructure.Messaging;
using Gozcu.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Gozcu.Infrastructure;

public sealed class PostgresHealthCheck : IHealthCheck
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly DatabaseReadiness _readiness;

    public PostgresHealthCheck(NpgsqlDataSource dataSource, DatabaseReadiness readiness)
    {
        _dataSource = dataSource;
        _readiness = readiness;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_readiness.IsReady)
        {
            return HealthCheckResult.Unhealthy("Veritabanı şeması henüz hazır değil.");
        }

        try
        {
            await using var command = _dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (NpgsqlException ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL'e ulaşılamıyor.", ex);
        }
    }
}

public sealed class RabbitMqHealthCheck : IHealthCheck
{
    private readonly RabbitMqConnection _connection;

    public RabbitMqHealthCheck(RabbitMqConnection connection) => _connection = connection;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var connection = await _connection.GetAsync(cancellationToken).ConfigureAwait(false);
            return connection.IsOpen ? HealthCheckResult.Healthy() : HealthCheckResult.Degraded("RabbitMQ bağlantısı kapalı.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Mesajlaşma olmadan da kayıt sürer; bu yüzden Unhealthy değil Degraded.
            return HealthCheckResult.Degraded("RabbitMQ'ya ulaşılamıyor.", ex);
        }
    }
}
