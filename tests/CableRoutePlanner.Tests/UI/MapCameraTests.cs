using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using CableRoutePlanner.UI.MapAdapters;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Projections;
using CableRoutePlanner.Domain;
using CableRoutePlanner.ViewModels;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class MapCameraTests
{
    [AvaloniaTheory]
    [InlineData(900, 600)]
    [InlineData(412, 860)]
    public void ZoomingOutCannotShrinkOfflineAreaBeyondOverview(int width, int height)
    {
        var control = new Mapsui.UI.Avalonia.MapControl();
        var window = new Window { Content = control, Width = width, Height = height };
        window.Show();
        using var adapter = new MapsuiMapAdapter(control, new(23.17, 51.23, 32.77, 56.18));
        adapter.InitializeViewport();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var overview = control.Map.Navigator.Viewport;

        for (var index = 0; index < 20; index++)
        {
            adapter.ZoomOut();
        }

        Assert.Equal(overview.Resolution, control.Map.Navigator.Viewport.Resolution, 6);
        // Wheel/pinch and direct navigation also pass through the same limiter.
        control.Map.Navigator.ZoomTo(overview.Resolution * 100);
        Assert.Equal(overview.Resolution, control.Map.Navigator.Viewport.Resolution, 6);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(-1, -1)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    [InlineData(1, 1)]
    public void PanningRetainsVisibleMapStripAndLetsAnEdgeReachScreenCentre(int horizontal, int vertical)
    {
        var control = new Mapsui.UI.Avalonia.MapControl();
        var window = new Window { Content = control, Width = 900, Height = 600 };
        window.Show();
        using var adapter = new MapsuiMapAdapter(control, new(23.17, 51.23, 32.77, 56.18));
        adapter.InitializeViewport();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        adapter.ZoomIn();
        adapter.ZoomIn();
        var resolution = control.Map.Navigator.Viewport.Resolution;
        var (left, bottom) = SphericalMercator.FromLonLat(23.17, 51.23);
        var (right, top) = SphericalMercator.FromLonLat(32.77, 56.18);
        control.Map.Navigator.CenterOnAndZoomTo(new MPoint(horizontal * 100000000, vertical * 100000000), resolution);
        var viewport = control.Map.Navigator.Viewport;

        Assert.Equal(resolution, viewport.Resolution);
        var visibleHalfWidth = viewport.Width * resolution / 2;
        var visibleHalfHeight = viewport.Height * resolution / 2;
        Assert.True(viewport.CenterX + visibleHalfWidth > left);
        Assert.True(viewport.CenterX - visibleHalfWidth < right);
        Assert.True(viewport.CenterY + visibleHalfHeight > bottom);
        Assert.True(viewport.CenterY - visibleHalfHeight < top);
        Assert.True(Math.Abs(viewport.CenterX - (horizontal < 0 ? left : right)) < visibleHalfWidth);
        Assert.True(Math.Abs(viewport.CenterY - (vertical < 0 ? bottom : top)) < visibleHalfHeight);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void RotatedViewportRetainsOfflineExtentAtEveryAngle(double angle)
    {
        var control = new Mapsui.UI.Avalonia.MapControl();
        var window = new Window { Content = control, Width = 900, Height = 600 };
        window.Show();
        var extent = new MRect(-500, -400, 500, 400);
        var limiter = new OfflineViewportLimiter(extent);
        var requested = control.Map.Navigator.Viewport with
        {
            CenterX = 100000,
            CenterY = -100000,
            Resolution = 0.0001,
            Rotation = angle
        };

        var limited = limiter.Limit(requested, null, null);
        var radians = angle * Math.PI / 180;
        var halfWidth = (requested.Width * Math.Abs(Math.Cos(radians))
            + requested.Height * Math.Abs(Math.Sin(radians))) * limited.Resolution / 2;
        var halfHeight = (requested.Width * Math.Abs(Math.Sin(radians))
            + requested.Height * Math.Abs(Math.Cos(radians))) * limited.Resolution / 2;

        Assert.True(limited.CenterX + halfWidth > extent.MinX);
        Assert.True(limited.CenterX - halfWidth < extent.MaxX);
        Assert.True(limited.CenterY + halfHeight > extent.MinY);
        Assert.True(limited.CenterY - halfHeight < extent.MaxY);
        Assert.Equal(angle, limited.Rotation);
        Assert.Equal(requested.Resolution, limited.Resolution);
        window.Close();
    }

    [AvaloniaFact]
    public void CompassResetReturnsNorthAndPreservesCameraCenterAndScale()
    {
        var control = new Mapsui.UI.Avalonia.MapControl();
        var window = new Window { Content = control, Width = 900, Height = 600 };
        window.Show();
        using var adapter = new MapsuiMapAdapter(control, new(27, 53, 28, 54));
        adapter.InitializeViewport();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        adapter.ZoomIn();
        adapter.ZoomIn();
        adapter.ZoomIn();
        var northAngle = double.NaN;
        adapter.NorthIndicatorRotationChanged += angle => northAngle = angle;
        var initial = control.Map.Navigator.Viewport;
        control.Map.Navigator.SetViewport(
            new Viewport(initial.CenterX, initial.CenterY, initial.Resolution, 45, initial.Width, initial.Height),
            0,
            new Mapsui.Animations.Easing(progress => progress));
        Assert.Equal(45, northAngle, 1);
        var before = control.Map.Navigator.Viewport;

        adapter.ResetNorth();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var after = control.Map.Navigator.Viewport;

        Assert.Equal(0, after.Rotation, 6);
        Assert.Equal(0, northAngle, 1);
        Assert.Equal(before.CenterX, after.CenterX, 6);
        Assert.Equal(before.CenterY, after.CenterY, 6);
        Assert.Equal(before.Resolution, after.Resolution, 6);
        window.Close();
    }

    [AvaloniaFact]
    public void RotationUpdatesCompassWithoutReloadingMapData()
    {
        var control = new Mapsui.UI.Avalonia.MapControl();
        var window = new Window { Content = control, Width = 900, Height = 600 };
        window.Show();
        using var adapter = new MapsuiMapAdapter(control, new(27, 53, 28, 54));
        adapter.InitializeViewport();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var mapDataEvents = 0;
        adapter.ViewportChanged += (_, _) => mapDataEvents++;
        var viewport = control.Map.Navigator.Viewport;

        control.Map.Navigator.SetViewport(
            new Viewport(viewport.CenterX, viewport.CenterY, viewport.Resolution, 45, viewport.Width, viewport.Height),
            0,
            new Mapsui.Animations.Easing(progress => progress));

        Assert.Equal(0, mapDataEvents);
        window.Close();
    }

    [AvaloniaFact]
    public void DesktopRightDragRotatesMapAndResetLeavesRotationAvailable()
    {
        var control = new Mapsui.UI.Avalonia.MapControl();
        var window = new Window { Content = control, Width = 900, Height = 600 };
        window.Show();
        using var adapter = new MapsuiMapAdapter(control, new(27, 53, 28, 54));
        adapter.InitializeViewport();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        window.MouseDown(new Avalonia.Point(700, 300), Avalonia.Input.MouseButton.Right);
        window.MouseMove(new Avalonia.Point(700, 350));
        window.MouseUp(new Avalonia.Point(700, 350), Avalonia.Input.MouseButton.Right);

        Assert.NotEqual(0, control.Map.Navigator.Viewport.Rotation);
        adapter.ResetNorth();
        Assert.Equal(0, control.Map.Navigator.Viewport.Rotation);
        window.MouseDown(new Avalonia.Point(700, 300), Avalonia.Input.MouseButton.Right);
        window.MouseMove(new Avalonia.Point(700, 325));
        window.MouseUp(new Avalonia.Point(700, 325), Avalonia.Input.MouseButton.Right);
        Assert.NotEqual(0, control.Map.Navigator.Viewport.Rotation);
        window.Close();
    }

    [AvaloniaFact]
    public void MapRouteVertexLayerAndPointerSelectionMatchListedNumber()
    {
        var control = new Mapsui.UI.Avalonia.MapControl();
        var window = new Window { Content = control, Width = 900, Height = 600 };
        window.Show();
        using var adapter = new MapsuiMapAdapter(control, new(27, 53, 28, 54));
        var vertex = new RouteVertexRow(7, new GeoCoordinate(53.5, 27.5), RouteVertexKind.Turn);
        adapter.SetRouteVertices([vertex]);
        adapter.InitializeViewport();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var selectedNumber = 0;
        adapter.RouteVertexSelected += number => selectedNumber = number;
        var (worldX, worldY) = SphericalMercator.FromLonLat(vertex.Coordinate.Longitude, vertex.Coordinate.Latitude);
        var screen = control.Map.Navigator.Viewport.WorldToScreen(worldX, worldY);

        window.MouseDown(new Avalonia.Point(screen.X, screen.Y), Avalonia.Input.MouseButton.Left);
        window.MouseUp(new Avalonia.Point(screen.X, screen.Y), Avalonia.Input.MouseButton.Left);

        Assert.Equal(7, selectedNumber);
        Assert.Single(control.Map.Layers.OfType<Mapsui.Layers.MemoryLayer>(),
            layer => layer.Name == "Вершины маршрута");
        var waypoint = new WaypointRow(Guid.NewGuid(), 1, new GeoCoordinate(53.55, 27.55));
        adapter.SetPoints([waypoint]);
        var selectedWaypoint = Guid.Empty;
        adapter.WaypointSelected += id => selectedWaypoint = id;
        var (waypointX, waypointY) = SphericalMercator.FromLonLat(
            waypoint.Coordinate.Longitude, waypoint.Coordinate.Latitude);
        var waypointScreen = control.Map.Navigator.Viewport.WorldToScreen(waypointX, waypointY);
        window.MouseDown(new Avalonia.Point(waypointScreen.X, waypointScreen.Y), Avalonia.Input.MouseButton.Left);
        window.MouseUp(new Avalonia.Point(waypointScreen.X, waypointScreen.Y), Avalonia.Input.MouseButton.Left);
        Assert.Equal(waypoint.Id, selectedWaypoint);
        window.Close();
    }
}
