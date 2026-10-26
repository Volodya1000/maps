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
        return services;
    }
}
