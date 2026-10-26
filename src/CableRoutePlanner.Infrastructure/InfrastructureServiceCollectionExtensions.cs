using CableRoutePlanner.Application;
using CableRoutePlanner.Infrastructure.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CableRoutePlanner.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddLogging();
        services.AddSingleton<IPointPlacementValidator, GeodataPointPlacementValidator>();
        services.AddSingleton<IGridGenerator, GridGenerator>();
        services.AddSingleton<IGridPathfinder, AStarGridPathfinder>();
        services.AddSingleton<IRoutePlanner>(provider => new GridRoutePlanner(
            provider.GetRequiredService<IGeodataRepository>(),
            provider.GetRequiredService<IGridGenerator>(),
            provider.GetRequiredService<IGridPathfinder>(),
            provider.GetRequiredService<ILogger<GridRoutePlanner>>()));
        return services;
    }
}
