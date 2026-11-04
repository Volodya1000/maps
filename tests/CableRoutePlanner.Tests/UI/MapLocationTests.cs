using Avalonia.Controls;
using Avalonia.Headless;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.UI.MapAdapters;
using Mapsui;
using Mapsui.Nts;
using Mapsui.Projections;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class MapLocationTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void LocationAndProgressLayersAreSeparateAndGPSIsAboveRouteObjects()
    {
        Mapsui.UI.Avalonia.MapControl control = new();
        using MapsuiMapAdapter adapter = new(control, new(27, 54, 28, 55));
        RouteAlternative route = Route();
        adapter.SetRoutes([route], route.Id);
        adapter.SetProgress(Progress(route));
        adapter.SetRouteVertices([new ViewModels.RouteVertexRow(1, route.Geometry[0])]);
        adapter.SetPoints([new ViewModels.WaypointRow(Guid.NewGuid(), 1, route.Geometry[0])]);
        adapter.SetLocation(new LocationMeasurement(route.Geometry[0], 8, Timestamp),
            new HeadingMeasurement(90, true, Timestamp), true);

        string[] names = control.Map.Layers.Select(layer => layer.Name).ToArray();

        Assert.True(Array.IndexOf(names, "Пройденный маршрут") > Array.IndexOf(names, "Маршруты"));
        Assert.True(Array.IndexOf(names, "Моё местоположение") > Array.IndexOf(names, "Точки"));
        Assert.Single(control.Map.Layers.OfType<Mapsui.Layers.MemoryLayer>(), layer => layer.Name == "Пройденный маршрут");
        Assert.Single(control.Map.Layers.OfType<Mapsui.Layers.MemoryLayer>(), layer => layer.Name == "Моё местоположение");
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void GPSLayerContainsAccuracyHeadingAndBlueMarkerWithoutAddingWaypoint()
    {
        Mapsui.UI.Avalonia.MapControl control = new();
        using MapsuiMapAdapter adapter = new(control, new(27, 54, 28, 55));
        adapter.SetPoints([]);
        adapter.SetLocation(new LocationMeasurement(new(54.5, 27.5), 25, Timestamp),
            new HeadingMeasurement(0, true, Timestamp), true);

        Mapsui.Layers.MemoryLayer location = Assert.Single(
            control.Map.Layers.OfType<Mapsui.Layers.MemoryLayer>(), layer => layer.Name == "Моё местоположение");
        Assert.Equal(3, location.Features.Count());
        Assert.Empty(Assert.Single(control.Map.Layers.OfType<Mapsui.Layers.MemoryLayer>(),
            layer => layer.Name == "Точки").Features);
        Mapsui.IFeature marker = Assert.Single(location.Features,
            feature => feature.Styles.OfType<Mapsui.Styles.SymbolStyle>().Any());
        Mapsui.Styles.SymbolStyle markerStyle = Assert.Single(marker.Styles.OfType<Mapsui.Styles.SymbolStyle>());
        Assert.Equal(new Mapsui.Styles.Color(32, 112, 220), markerStyle.Fill!.Color);
        Assert.Equal(Mapsui.Styles.Color.White, markerStyle.Outline!.Color);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void MissingHeadingHidesSectorButKeepsGPSMarkerAndAccuracy()
    {
        Mapsui.UI.Avalonia.MapControl control = new();
        using MapsuiMapAdapter adapter = new(control, new(27, 54, 28, 55));

        adapter.SetLocation(new LocationMeasurement(new(54.5, 27.5), 25, Timestamp), null, true);

        Mapsui.Layers.MemoryLayer location = Assert.Single(
            control.Map.Layers.OfType<Mapsui.Layers.MemoryLayer>(), layer => layer.Name == "Моё местоположение");
        Assert.Equal(2, location.Features.Count());
        Assert.Single(location.Features, feature => feature.Styles.OfType<Mapsui.Styles.SymbolStyle>().Any());
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void FirstFixDoesNotMoveCameraButExplicitCenterEnablesFollow()
    {
        Mapsui.UI.Avalonia.MapControl control = new();
        Window window = new() { Content = control, Width = 900, Height = 600 };
        window.Show();
        using MapsuiMapAdapter adapter = new(control, new(27, 54, 28, 55));
        adapter.InitializeViewport();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Viewport before = control.Map.Navigator.Viewport;
        GeoCoordinate coordinate = new(54.5, 27.5);

        adapter.SetLocation(new LocationMeasurement(coordinate, 10, Timestamp), null, true);
        Viewport afterFix = control.Map.Navigator.Viewport;
        adapter.CenterOnLocation();

        Assert.Equal(before.CenterX, afterFix.CenterX, 6);
        Assert.Equal(before.CenterY, afterFix.CenterY, 6);
        Assert.True(adapter.IsFollowing);
        (double x, _) = SphericalMercator.FromLonLat(coordinate.Longitude, coordinate.Latitude);
        Assert.Equal(x, control.Map.Navigator.Viewport.CenterX, 1);
        window.Close();
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void ManualPanDisablesFollowWhileGPSUpdatesRemainIndependent()
    {
        Mapsui.UI.Avalonia.MapControl control = new();
        Window window = new() { Content = control, Width = 900, Height = 600 };
        window.Show();
        using MapsuiMapAdapter adapter = new(control, new(27, 54, 28, 55));
        adapter.InitializeViewport();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        GeoCoordinate first = new(54.5, 27.5);
        adapter.SetLocation(new LocationMeasurement(first, 10, Timestamp), null, true);
        adapter.CenterOnLocation();

        window.MouseDown(new Avalonia.Point(450, 300), Avalonia.Input.MouseButton.Left);
        window.MouseMove(new Avalonia.Point(520, 320));
        window.MouseUp(new Avalonia.Point(520, 320), Avalonia.Input.MouseButton.Left);
        adapter.SetLocation(new LocationMeasurement(new(54.51, 27.51), 10, Timestamp.AddSeconds(1)), null, true);

        Assert.False(adapter.IsFollowing);
        window.Close();
    }

    private static RouteAlternative Route()
    {
        GeoCoordinate[] geometry = [new(54.3, 27.3), new(54.3, 27.6)];
        return new(Guid.NewGuid(), "Тестовый маршрут", geometry,
            new RouteMetrics(20000, 0, 20000, 0, 0, 0, 20000, null));
    }

    private static RouteProgressState Progress(RouteAlternative route) => new(
        "test",
        route.Metrics.LengthMetres,
        [new ProgressInterval(0, 1000)],
        1000,
        route.Metrics.LengthMetres - 1000,
        5,
        [route.Geometry],
        ProgressTrackingStatus.Active,
        LocationQuality.Good);
}
