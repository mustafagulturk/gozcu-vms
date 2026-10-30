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
}
