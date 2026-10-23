using Avalonia.Controls;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Persistence;
using CableRoutePlanner.Infrastructure.Routing;
using CableRoutePlanner.UI;
using CableRoutePlanner.UI.MapAdapters;
using CableRoutePlanner.UI.Views;
using CableRoutePlanner.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace CableRoutePlanner.Bootstrap;

public static class AppBootstrap
{
    public static ServiceProvider CreateServices(
        string dataDirectory,
        string settingsPath,
        IClipboardService? clipboard = null,
        Func<IProgress<string>, CancellationToken, Task>? prepareData = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IGeodataRepository>(_ => new SqliteGeodataRepository(dataDirectory));
        services.AddSingleton<ISettingsStore>(_ => new JsonSettingsStore(settingsPath));
        services.AddSingleton<IRoutePlanner, GridRoutePlanner>();
        services.AddSingleton<IPlanningSession, PlanningSession>();
        services.AddSingleton<IClipboardService>(clipboard ?? new AvaloniaClipboardService());
        services.AddSingleton<IScreen, NavigationHost>();
        services.AddSingleton<MapViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<Func<Mapsui.UI.Avalonia.MapControl, GeoBounds, IMapAdapter>>(
            _ => (control, bounds) => new MapsuiMapAdapter(control, bounds));
        services.AddTransient<MapView>();
        services.AddTransient<SettingsView>();
        services.AddSingleton<Func<MapView>>(sp => () => sp.GetRequiredService<MapView>());
        services.AddSingleton<Func<SettingsView>>(sp => () => sp.GetRequiredService<SettingsView>());
        services.AddSingleton<TypedViewLocator>();
        services.AddTransient<ShellView>();
        services.AddSingleton<Func<ShellView>>(sp => () => sp.GetRequiredService<ShellView>());
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
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public static PlannerApplication CreateApplication(string dataDirectory, string settingsPath)
    {
        var app = new PlannerApplication();
        ConfigureApplication(app, dataDirectory, settingsPath);
        return app;
    }

    public static void ConfigureApplication(
        PlannerApplication app,
        string dataDirectory,
        string settingsPath,
        Func<IProgress<string>, CancellationToken, Task>? prepareData = null)
    {
        var services = CreateServices(dataDirectory, settingsPath, prepareData: prepareData);
        app.ServiceLifetime = services;
        app.ContentFactory = () =>
        {
            try
            {
                return services.GetRequiredService<StartupView>();
            }
            catch (Exception ex)
            {
                return new ScrollViewer
                {
                    Content = new StackPanel
                    {
                        Margin = new Avalonia.Thickness(28),
                        Spacing = 16,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "Не удалось открыть офлайн-карту",
                                FontSize = 24
                            },
                            new TextBlock
                            {
                                Text = ex.GetBaseException().Message,
                                TextWrapping = Avalonia.Media.TextWrapping.Wrap
                            },
                            new TextBlock
                            {
                                Text = $"Каталог данных: {dataDirectory}\nПроверьте наличие manifest.json и geodata.sqlite. Для Desktop путь задаётся CABLEROUTE_DATA. После исправления перезапустите приложение.",
                                TextWrapping = Avalonia.Media.TextWrapping.Wrap
                            }
                        }
                    }
                };
            }
        };
    }
}
