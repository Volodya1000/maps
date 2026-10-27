using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Mapsui.UI.Avalonia;
using Microsoft.Data.Sqlite;

namespace CableRoutePlanner.Windows7Prototype;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<PrototypeApplication>()
        .UsePlatformDetect()
        .LogToTrace();
}

internal sealed class PrototypeApplication : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Window
            {
                Width = 800,
                Height = 600,
                Title = "Windows 7 compatibility prototype",
                Content = new MapControl
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
