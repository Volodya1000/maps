using Avalonia;
using Avalonia.ReactiveUI;
using CableRoutePlanner.Composition;

namespace CableRoutePlanner.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        var data = Environment.GetEnvironmentVariable("CABLEROUTE_DATA") ?? Path.Combine(AppContext.BaseDirectory, "Data");
        var settings = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CableRoutePlanner",
            "settings.json");
        return AppBuilder.Configure(() => AvaloniaAppBootstrap.CreateApplication(data, settings)).UsePlatformDetect().UseReactiveUI().LogToTrace();
    }
}
