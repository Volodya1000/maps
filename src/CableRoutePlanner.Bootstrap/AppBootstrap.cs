using CableRoutePlanner.Application;
using CableRoutePlanner.Persistence;
using CableRoutePlanner.Infrastructure.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CableRoutePlanner.Bootstrap;

public static class AppBootstrap
{
    public static ServiceProvider CreateServices(string dataDirectory, string settingsPath) =>
        CreateServiceCollection(dataDirectory, settingsPath).BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

    public static ServiceCollection CreateServiceCollection(
        string dataDirectory,
        string settingsPath,
        IClipboardService? clipboard = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddConsole());
        services.AddSingleton<IGeodataRepository>(_ => new SqliteGeodataRepository(dataDirectory));
        services.AddSingleton<ISettingsStore>(_ => new JsonSettingsStore(settingsPath));
        services.AddSingleton<IGridGenerator, GridGenerator>();
        services.AddSingleton<IGridPathfinder, AStarGridPathfinder>();
        services.AddSingleton<IRoutePlanner, GridRoutePlanner>();
        services.AddSingleton<IPlanningSession, PlanningSession>();
        if (clipboard is not null)
        {
            services.AddSingleton(clipboard);
        }

        return services;
    }
}
