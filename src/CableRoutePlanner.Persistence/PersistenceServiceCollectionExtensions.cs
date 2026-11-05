using CableRoutePlanner.Application;
using Microsoft.Extensions.DependencyInjection;

namespace CableRoutePlanner.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        string dataDirectory,
        string settingsPath)
    {
        services.AddSingleton<IGeodataRepository>(_ => new SqliteGeodataRepository(dataDirectory));
        services.AddSingleton<ISettingsStore>(_ => new JsonSettingsStore(settingsPath));
        services.AddSingleton<ILocationPreferencesStore>(_ => new JsonLocationPreferencesStore(
            Path.Combine(dataDirectory, "location-settings.json")));
        services.AddSingleton<ITrackingSessionStore>(_ => new JsonTrackingSessionStore(
            Path.Combine(dataDirectory, "tracking-session.json")));
        return services;
    }
}
