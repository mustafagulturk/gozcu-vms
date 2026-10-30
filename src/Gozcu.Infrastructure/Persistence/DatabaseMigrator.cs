using System.Reflection;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Gozcu.Infrastructure.Persistence;

/// <summary>
/// Gömülü SQL betiklerini sırayla uygular. Birden fazla servis aynı anda başlasa da
/// PostgreSQL advisory lock sayesinde betikler yalnızca bir kez çalışır.
/// </summary>
public sealed class DatabaseMigrator
{
    // "gozcu" kelimesinden türetilmiş sabit kilit anahtarı.
    private const long AdvisoryLockKey = 0x676F7A6375;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<DatabaseMigrator> _logger;

    public DatabaseMigrator(NpgsqlDataSource dataSource, ILogger<DatabaseMigrator> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_lock(@key)", new { key = AdvisoryLockKey }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version    text        PRIMARY KEY,
                    applied_at timestamptz NOT NULL DEFAULT now()
                )
                """,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            var applied = (await connection.QueryAsync<string>(new CommandDefinition(
                "SELECT version FROM schema_migrations", cancellationToken: cancellationToken)).ConfigureAwait(false)).ToHashSet();

            foreach (var (version, sql) in LoadScripts())
            {
                if (applied.Contains(version))
                {
                    continue;
                }

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO schema_migrations (version) VALUES (@version)",
                    new { version },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Veritabanı betiği uygulandı: {Version}", version);
            }
        }
        finally
        {
            await connection.ExecuteAsync("SELECT pg_advisory_unlock(@key)", new { key = AdvisoryLockKey }).ConfigureAwait(false);
        }
    }

    internal static IEnumerable<(string Version, string Sql)> LoadScripts()
    {
        var assembly = typeof(DatabaseMigrator).Assembly;
        const string prefix = "Gozcu.Infrastructure.Persistence.Migrations.";
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            yield return (Path.GetFileNameWithoutExtension(name[prefix.Length..]), reader.ReadToEnd());
        }
    }
}
