using CableRoutePlanner.Application;
using CableRoutePlanner.Bootstrap;
using CableRoutePlanner.Domain;
using CableRoutePlanner.UI;
using CableRoutePlanner.UI.MapAdapters;
using CableRoutePlanner.UI.Views;
using CableRoutePlanner.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace CableRoutePlanner.Composition;

public static class AvaloniaAppBootstrap
{
    public static PlannerApplication CreateApplication(string dataDirectory, string settingsPath) =>
        ConfigureApplication(new PlannerApplication(), dataDirectory, settingsPath);

    public static PlannerApplication ConfigureApplication(PlannerApplication app, string dataDirectory, string settingsPath,
        Func<IProgress<string>, CancellationToken, Task>? prepareData = null)
    {
        var services = CreateServices(dataDirectory, settingsPath, new AvaloniaClipboardService(), prepareData);
        app.ServiceLifetime = services;
        app.DataDirectory = dataDirectory;
        app.ContentFactory = () => services.GetRequiredService<StartupView>();
        return app;
    }

    public static ServiceProvider CreateServices(string dataDirectory, string settingsPath, IClipboardService clipboard, Func<IProgress<string>, CancellationToken, Task>? prepareData = null)
    {
        var services = AppBootstrap.CreateServiceCollection(dataDirectory, settingsPath, clipboard);
        services.AddSingleton<IScreen, NavigationHost>();
        services.AddSingleton<MapViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<Func<Mapsui.UI.Avalonia.MapControl, GeoBounds, IMapAdapter>>(_ => (c, b) => new MapsuiMapAdapter(c, b));
        services.AddTransient<MapView>();
        services.AddTransient<SettingsView>();
        services.AddTransient<ShellView>();
        services.AddSingleton<Func<MapView>>(sp => () => sp.GetRequiredService<MapView>());
        services.AddSingleton<Func<SettingsView>>(sp => () => sp.GetRequiredService<SettingsView>());
        services.AddSingleton<Func<ShellView>>(sp => () => sp.GetRequiredService<ShellView>());
        services.AddSingleton<TypedViewLocator>();
        services.AddSingleton<StartupViewModel>(sp => new StartupViewModel(async (progress, ct) =>
        {
            if (prepareData is not null)
            {
                await prepareData(progress, ct);
            }

            progress.Report("Проверка геоданных…");
            await Task.Run(() => sp.GetRequiredService<IGeodataRepository>(), ct);
            return sp.GetRequiredService<ShellViewModel>();
        }));
        services.AddTransient<StartupView>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
