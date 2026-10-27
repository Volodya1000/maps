using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Projections;
using Mapsui.UI.Avalonia;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace CableRoutePlanner.Windows7Prototype;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string databasePath = Path.Combine(Path.GetTempPath(), "maps-win7-prototype.sqlite");
        try
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }

            using SqliteConnection connection = new($"Data Source={databasePath}");
            connection.Open();
            using SqliteCommand create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE points (name TEXT NOT NULL, latitude REAL NOT NULL, longitude REAL NOT NULL);";
            create.ExecuteNonQuery();
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO points VALUES ('Minsk', 53.9, 27.56);";
            insert.ExecuteNonQuery();
            using SqliteCommand read = connection.CreateCommand();
            read.CommandText = "SELECT name, latitude, longitude FROM points;";
            using SqliteDataReader reader = read.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException("SQLite fixture did not contain a point.");
            }

            string name = reader.GetString(0);
            double latitude = reader.GetDouble(1);
            double longitude = reader.GetDouble(2);
            if (name != "Minsk")
            {
                throw new InvalidOperationException("SQLite fixture returned an unexpected point.");
            }

            BuildAvaloniaApp(latitude, longitude).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            string diagnostics = Path.Combine(Path.GetTempPath(), "maps-win7-prototype-error.log");
            File.WriteAllText(diagnostics, exception.ToString(), System.Text.Encoding.UTF8);
            throw;
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private static AppBuilder BuildAvaloniaApp(double latitude, double longitude)
    {
        PrototypeApplication.Options = new PrototypeMapOptions(latitude, longitude);
        return AppBuilder.Configure<PrototypeApplication>()
        .UsePlatformDetect()
        .LogToTrace();
    }
}

internal sealed class PrototypeMapOptions
{
    internal PrototypeMapOptions(double latitude, double longitude)
    {
        Latitude = latitude;
        Longitude = longitude;
    }

    internal double Latitude { get; }

    internal double Longitude { get; }
}

internal sealed class PrototypeApplication : Application
{
    internal static PrototypeMapOptions Options { get; set; } = new(53.9, 27.56);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MapControl map = new();
            map.Map = new Mapsui.Map();
            (double x, double y) = SphericalMercator.FromLonLat(Options.Longitude, Options.Latitude);
            map.Map.Layers.Add(new MemoryLayer
            {
                Name = "SQLite fixture",
                Features = new[] { new PointFeature(new MPoint(x, y)) }
            });
            desktop.MainWindow = new Window
            {
                Width = 800,
                Height = 600,
                Title = "Windows 7 compatibility prototype",
                Content = map
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
