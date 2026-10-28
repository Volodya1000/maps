using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.UI.MapAdapters;
using CableRoutePlanner.UI.Views;
using CableRoutePlanner.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace CableRoutePlanner.UI;

public static class PresentationServiceCollectionExtensions
{
    public static IServiceCollection AddPresentation(
        this IServiceCollection services,
        Func<IProgress<string>, CancellationToken, Task>? prepareData = null,
        IClipboardService? clipboard = null)
    {
        if (clipboard is null)
        {
            services.AddSingleton<IClipboardService, AvaloniaClipboardService>();
        }
        else
        {
            services.AddSingleton(clipboard);
        }
        services.AddSingleton<IScreen, NavigationHost>();
        services.AddSingleton<MapViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<Func<Mapsui.UI.Avalonia.MapControl, GeoBounds, IMapAdapter>>(
            provider => (control, bounds) => new MapsuiMapAdapter(
                control,
                bounds,
                provider.GetRequiredService<IGeodataRepository>()));
        services.AddTransient<MapView>();
        services.AddTransient<SettingsView>();
        services.AddTransient<ShellView>();
        services.AddSingleton<Func<MapView>>(provider => () => provider.GetRequiredService<MapView>());
        services.AddSingleton<Func<SettingsView>>(provider => () => provider.GetRequiredService<SettingsView>());
        services.AddSingleton<Func<ShellView>>(provider => () => provider.GetRequiredService<ShellView>());
        services.AddSingleton<TypedViewLocator>();
        services.AddSingleton<StartupViewModel>(provider => new StartupViewModel(async (progress, ct) =>
        {
            if (prepareData is not null)
            {
                await prepareData(progress, ct);
            }

            progress.Report("Проверка геоданных…");
            await Task.Run(() => provider.GetRequiredService<IGeodataRepository>(), ct);
            return provider.GetRequiredService<ShellViewModel>();
        }));
        services.AddTransient<StartupView>();
        return services;
    }

    public static void ConfigureApplication(
        PlannerApplication app,
        IServiceProvider services,
        string dataDirectory)
    {
        app.ServiceLifetime = services as IDisposable;
        app.DataDirectory = dataDirectory;
        app.ContentFactory = () => services.GetRequiredService<StartupView>();
    }
}
