using Gozcu.Infrastructure.Cameras;
using Gozcu.Infrastructure.Messaging;
using Gozcu.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Gozcu.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGozcuPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(DatabaseOptions.ConnectionStringName)
            ?? throw new InvalidOperationException($"ConnectionStrings:{DatabaseOptions.ConnectionStringName} tanımlı değil.");

        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<DatabaseReadiness>();
        services.AddSingleton<DatabaseMigrator>();
        services.AddSingleton<CameraRepository>();
        services.AddSingleton<SegmentRepository>();
        services.AddSingleton<DetectionRepository>();
        services.AddHostedService<DatabaseMigrationService>();
        services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);
        return services;
    }

    public static IServiceCollection AddGozcuMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.SectionName));
        services.AddSingleton<RabbitMqConnection>();
        services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();
        services.AddHealthChecks().AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: ["ready"]);
        return services;
    }

    /// <summary>
    /// Veritabanındaki kamera listesinin bellekteki kopyasını (<see cref="ICameraCatalog"/>) ve onu güncel tutan arka plan
    /// servisini kaydeder. <see cref="AddGozcuPersistence"/> ve <see cref="AddGozcuMessaging"/> gerektirir.
    /// </summary>
    public static IServiceCollection AddGozcuCameraCatalog(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CameraCatalogOptions>(configuration.GetSection(CameraCatalogOptions.SectionName));
        services.AddSingleton<CameraCatalog>();
        services.AddSingleton<ICameraCatalog>(sp => sp.GetRequiredService<CameraCatalog>());
        services.AddSingleton<RabbitMqEventSubscriber>();
        services.AddSingleton<CameraCatalogRefresher>();
        services.AddHostedService(sp => sp.GetRequiredService<CameraCatalogRefresher>());
        return services;
    }
}
