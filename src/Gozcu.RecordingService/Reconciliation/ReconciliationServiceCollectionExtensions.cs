namespace Gozcu.RecordingService.Reconciliation;

public static class ReconciliationServiceCollectionExtensions
{
    public static IServiceCollection AddStartupReconciliation(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapReconciliationApi(this IEndpointRouteBuilder app) => app;
}
