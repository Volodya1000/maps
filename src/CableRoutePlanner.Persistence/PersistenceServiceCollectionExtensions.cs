using CableRoutePlanner.Application;
using Microsoft.Extensions.DependencyInjection;

namespace CableRoutePlanner.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        string dataDirectory,
        string settingsPath,
        string? elevationTemporaryDirectory = null)
    {
        services.AddSingleton<IGeodataRepository>(_ => new SqliteGeodataRepository(dataDirectory));
        services.AddSingleton<IElevationProvider>(_ => File.Exists(Path.Combine(dataDirectory, "elevation", "index.json"))
            ? new TiledElevationProvider(dataDirectory, elevationTemporaryDirectory)
            : new SqliteElevationProvider(dataDirectory, elevationTemporaryDirectory));
        services.AddSingleton<ISettingsStore>(_ => new JsonSettingsStore(settingsPath));
        services.AddSingleton<ILocationPreferencesStore>(_ => new JsonLocationPreferencesStore(
            Path.Combine(dataDirectory, "location-settings.json")));
        services.AddSingleton<ITrackingSessionStore>(_ => new JsonTrackingSessionStore(
            Path.Combine(dataDirectory, "tracking-session.json")));
        services.AddSingleton<ISavedRouteStore>(_ => new JsonSavedRouteStore(
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, "saved-routes.json")));
        return services;
    }
}
