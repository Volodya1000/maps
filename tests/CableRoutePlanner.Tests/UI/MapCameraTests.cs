using Avalonia.Controls;
using Avalonia.Media;
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
    private sealed class RenderCountingMapControl : Mapsui.UI.Avalonia.MapControl
    {
        public int RenderCount { get; private set; }

        public override void Render(DrawingContext context)
        {
            RenderCount++;
            base.Render(context);
        }
    }

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
        Viewport overview = control.Map.Navigator.Viewport;

        for (int index = 0; index < 20; index++)
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
    public void PanningKeepsHalfOfMapViewportVisibleAndLetsAnEdgeReachScreenCentre(int horizontal, int vertical)
    {
        var control = new Mapsui.UI.Avalonia.MapControl();
        var window = new Window { Content = control, Width = 900, Height = 600 };
        window.Show();
        using var adapter = new MapsuiMapAdapter(control, new(23.17, 51.23, 32.77, 56.18));
        adapter.InitializeViewport();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        adapter.ZoomIn();
        adapter.ZoomIn();
        double resolution = control.Map.Navigator.Viewport.Resolution;
        (double left, double bottom) = SphericalMercator.FromLonLat(23.17, 51.23);
        (double right, double top) = SphericalMercator.FromLonLat(32.77, 56.18);
        control.Map.Navigator.CenterOnAndZoomTo(new MPoint(horizontal * 100000000, vertical * 100000000), resolution);
        Viewport viewport = control.Map.Navigator.Viewport;

        Assert.Equal(resolution, viewport.Resolution);
        double visibleHalfWidth = viewport.Width * resolution / 2;
        double visibleHalfHeight = viewport.Height * resolution / 2;
        double horizontalOverlap = Math.Min(right, viewport.CenterX + visibleHalfWidth)
            - Math.Max(left, viewport.CenterX - visibleHalfWidth);
        double verticalOverlap = Math.Min(top, viewport.CenterY + visibleHalfHeight)
            - Math.Max(bottom, viewport.CenterY - visibleHalfHeight);
        Assert.True(horizontalOverlap >= Math.Min(right - left, visibleHalfWidth) - 0.001);
        Assert.True(verticalOverlap >= Math.Min(top - bottom, visibleHalfHeight) - 0.001);
        Assert.InRange(viewport.CenterX, left, right);
        Assert.InRange(viewport.CenterY, bottom, top);
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
    public void RotatedViewportKeepsHalfOfMapViewportVisibleAtEveryAngle(double angle)
    {
        var control = new Mapsui.UI.Avalonia.MapControl();
        var window = new Window { Content = control, Width = 900, Height = 600 };
        window.Show();
        var extent = new MRect(-500, -400, 500, 400);
        var limiter = new OfflineViewportLimiter(extent);
        Viewport requested = control.Map.Navigator.Viewport with
        {
            CenterX = 100000,
            CenterY = -100000,
            Resolution = 0.0001,
            Rotation = angle
        };

        Viewport limited = limiter.Limit(requested, null, null);
        double radians = angle * Math.PI / 180;
        double halfWidth = (requested.Width * Math.Abs(Math.Cos(radians))
            + requested.Height * Math.Abs(Math.Sin(radians))) * limited.Resolution / 2;
        double halfHeight = (requested.Width * Math.Abs(Math.Sin(radians))
            + requested.Height * Math.Abs(Math.Cos(radians))) * limited.Resolution / 2;

        double horizontalOverlap = Math.Min(extent.MaxX, limited.CenterX + halfWidth)
            - Math.Max(extent.MinX, limited.CenterX - halfWidth);
        double verticalOverlap = Math.Min(extent.MaxY, limited.CenterY + halfHeight)
            - Math.Max(extent.MinY, limited.CenterY - halfHeight);
        Assert.True(horizontalOverlap >= Math.Min(extent.Width, halfWidth) - 0.001);
        Assert.True(verticalOverlap >= Math.Min(extent.Height, halfHeight) - 0.001);
        Assert.InRange(limited.CenterX, extent.MinX, extent.MaxX);
        Assert.InRange(limited.CenterY, extent.MinY, extent.MaxY);
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
        double northAngle = double.NaN;
        adapter.NorthIndicatorRotationChanged += angle => northAngle = angle;
        Viewport initial = control.Map.Navigator.Viewport;
        control.Map.Navigator.SetViewport(
            new Viewport(initial.CenterX, initial.CenterY, initial.Resolution, 45, initial.Width, initial.Height),
            0,
            new Mapsui.Animations.Easing(progress => progress));
        Assert.Equal(45, northAngle, 1);
        Viewport before = control.Map.Navigator.Viewport;

        adapter.ResetNorth();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Viewport after = control.Map.Navigator.Viewport;

        Assert.Equal(0, after.Rotation, 6);
        Assert.Equal(0, northAngle, 1);
        Assert.Equal(before.CenterX, after.CenterX, 6);
        Assert.Equal(before.CenterY, after.CenterY, 6);
        Assert.Equal(before.Resolution, after.Resolution, 6);
        window.Close();
    }

    [AvaloniaFact]
    public async Task CompassResetRequestsARenderWithoutAnotherPointerEvent()
    {
        var control = new RenderCountingMapControl();
        var window = new Window { Content = control, Width = 900, Height = 600 };
        window.Show();
        using var adapter = new MapsuiMapAdapter(control, new(27, 53, 28, 54));
        adapter.InitializeViewport();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Viewport initial = control.Map.Navigator.Viewport;
        control.Map.Navigator.SetViewport(
            initial with { Rotation = 45 },
            0,
            new Mapsui.Animations.Easing(progress => progress));
        await Task.Delay(control.UpdateInterval * 2);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        int rendersBeforeReset = control.RenderCount;

        adapter.ResetNorth();
        await Task.Delay(control.UpdateInterval * 2);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(control.RenderCount > rendersBeforeReset);
        Assert.Equal(0, control.Map.Navigator.Viewport.Rotation, 6);
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
        int mapDataEvents = 0;
        adapter.ViewportChanged += (_, _) => mapDataEvents++;
        Viewport viewport = control.Map.Navigator.Viewport;

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
    public void BridgeTransitionsAndUserWaypointsDoNotDuplicateMapMarkers()
    {
        var control = new Mapsui.UI.Avalonia.MapControl();
        using var adapter = new MapsuiMapAdapter(control, new(27, 53, 28, 54));
        adapter.SetRouteVertices(
        [
            new RouteVertexRow(1, new GeoCoordinate(53.4, 27.4), RouteVertexKind.Transition),
            new RouteVertexRow(2, new GeoCoordinate(53.5, 27.5), RouteVertexKind.UserWaypoint),
            new RouteVertexRow(3, new GeoCoordinate(53.6, 27.6), RouteVertexKind.Turn)
        ]);

        Mapsui.Layers.MemoryLayer layer = Assert.Single(control.Map.Layers.OfType<Mapsui.Layers.MemoryLayer>(),
            candidate => candidate.Name == "Вершины маршрута");
        Assert.Single(layer.Features);
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
        int selectedNumber = 0;
        adapter.RouteVertexSelected += number => selectedNumber = number;
        (double worldX, double worldY) = SphericalMercator.FromLonLat(vertex.Coordinate.Longitude, vertex.Coordinate.Latitude);
        MPoint screen = control.Map.Navigator.Viewport.WorldToScreen(worldX, worldY);

        Click(window, new Avalonia.Point(screen.X, screen.Y));

        Assert.Equal(7, selectedNumber);
        Assert.Single(control.Map.Layers.OfType<Mapsui.Layers.MemoryLayer>(),
            layer => layer.Name == "Вершины маршрута");
        var waypoint = new WaypointRow(Guid.NewGuid(), 1, new GeoCoordinate(53.55, 27.55));
        adapter.SetPoints([waypoint]);
        Guid selectedWaypoint = Guid.Empty;
        adapter.WaypointSelected += id => selectedWaypoint = id;
        (double waypointX, double waypointY) = SphericalMercator.FromLonLat(
            waypoint.Coordinate.Longitude, waypoint.Coordinate.Latitude);
        MPoint waypointScreen = control.Map.Navigator.Viewport.WorldToScreen(waypointX, waypointY);
        Click(window, new Avalonia.Point(waypointScreen.X, waypointScreen.Y));
        Assert.Equal(waypoint.Id, selectedWaypoint);
        window.Close();
    }

    private static void Click(Window window, Avalonia.Point position)
    {
        // Headless hit testing is updated by pointer movement. Without it a click can
        // occasionally retain the target from the preceding test window on Linux CI.
        window.MouseMove(position);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.MouseDown(position, Avalonia.Input.MouseButton.Left);
        window.MouseUp(position, Avalonia.Input.MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }
}
