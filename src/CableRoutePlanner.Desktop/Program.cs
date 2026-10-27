using Avalonia;
using Avalonia.ReactiveUI;
using CableRoutePlanner.Application;
using CableRoutePlanner.Infrastructure;
using CableRoutePlanner.Persistence;
using CableRoutePlanner.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CableRoutePlanner.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        string data = Environment.GetEnvironmentVariable("CABLEROUTE_DATA") ?? Path.Combine(AppContext.BaseDirectory, "Data");
        string settings = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CableRoutePlanner",
            "settings.json");
        return AppBuilder.Configure(() =>
        {
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.AddConsole());
            services.AddInfrastructure();
            services.AddPersistence(data, settings);
            services.AddSingleton<IPlanningSession, PlanningSession>();
            services.AddPresentation();
            ServiceProvider provider = services.BuildServiceProvider(
                new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            var app = new PlannerApplication();
            PresentationServiceCollectionExtensions.ConfigureApplication(app, provider, data);
            return app;
        }).UsePlatformDetect().UseReactiveUI().LogToTrace();
    }
}
