using Dapper;
using Gozcu.Contracts.Cameras;
using Npgsql;

namespace Gozcu.Infrastructure.Persistence;

public sealed class CameraRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public CameraRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task UpsertAsync(CameraDefinition camera, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(camera);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO cameras (id, name, rtsp_url, enabled)
            VALUES (@Id, @Name, @RtspUrl, @Enabled)
            ON CONFLICT (id) DO UPDATE
               SET name = EXCLUDED.name,
                   rtsp_url = EXCLUDED.rtsp_url,
                   enabled = EXCLUDED.enabled,
                   updated_at = now()
            """,
            new { camera.Id, camera.Name, RtspUrl = camera.RtspUrl?.ToString() ?? string.Empty, camera.Enabled },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
