using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Projections;
using Mapsui.Styles;
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

            BuildAvaloniaApp(name, latitude, longitude).StartWithClassicDesktopLifetime(args);
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

    private static AppBuilder BuildAvaloniaApp(string name, double latitude, double longitude)
    {
        PrototypeApplication.Options = new PrototypeMapOptions(name, latitude, longitude);
        return AppBuilder.Configure<PrototypeApplication>()
        .UsePlatformDetect()
        .LogToTrace();
    }
}

internal sealed class PrototypeMapOptions
{
    internal PrototypeMapOptions(string name, double latitude, double longitude)
    {
        Name = name;
        Latitude = latitude;
        Longitude = longitude;
    }

    internal string Name { get; }

    internal double Latitude { get; }

    internal double Longitude { get; }
}

internal sealed class PrototypeApplication : Application
{
    internal static PrototypeMapOptions Options { get; set; } = new("Minsk", 53.9, 27.56);

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
                Features = new[]
                {
                    CreateMarker(x, y)
                }
            });
            map.Map.Navigator.SetViewport(
                new Viewport(x, y, 1000, 0, 800, 600),
                0,
                new Mapsui.Animations.Easing(progress => progress));
            map.RefreshGraphics();
            string readinessPath = Environment.GetEnvironmentVariable("WIN7_PROTOTYPE_READY_PATH")
                ?? Path.Combine(Path.GetTempPath(), "maps-win7-prototype-ready.json");
            File.WriteAllText(
                readinessPath,
                $"{{\"schema\":\"maps.win7.prototype.ready.v1\",\"point\":\"{Options.Name}\",\"latitude\":{Options.Latitude.ToString(CultureInfo.InvariantCulture)},\"longitude\":{Options.Longitude.ToString(CultureInfo.InvariantCulture)},\"mapLayer\":\"SQLite fixture\",\"viewport\":\"centered\",\"renderMarker\":\"#dc1e1e\"}}\n",
                System.Text.Encoding.UTF8);
            desktop.MainWindow = new Window
            {
                Width = 800,
                Height = 600,
                Title = $"Windows7Prototype | SQLite={Options.Name} | MapPoint={Options.Latitude.ToString(CultureInfo.InvariantCulture)},{Options.Longitude.ToString(CultureInfo.InvariantCulture)}",
                Content = map
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static PointFeature CreateMarker(double x, double y)
    {
        PointFeature feature = new(new MPoint(x, y));
        feature.Styles.Add(new SymbolStyle
        {
            Fill = new Brush(new Color(220, 30, 30)),
            Outline = new Pen(Color.White, 2),
            SymbolScale = 2
        });
        return feature;
    }
}
