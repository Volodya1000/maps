using Avalonia.Controls;
using System.Reactive.Linq;
using Avalonia.Media;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using CableRoutePlanner.Application;
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
    public void IntermediateVerticesOfOneLegAppearTogetherAndCanBeForcedVisible()
    {
        var control = new Mapsui.UI.Avalonia.MapControl();
        using var adapter = new MapsuiMapAdapter(control, new(27, 54, 27.006, 54.006));
        RouteVertexRow first = new(2, TestData.P(10, 150), LegIndex: 1, LegSubIndex: 1);
        RouteVertexRow second = new(3, TestData.P(100, 150), LegIndex: 1, LegSubIndex: 2);
        RouteVertexRow parent = new(1, TestData.P(0, 150), RouteVertexKind.UserWaypoint, LegIndex: 1);
        Mapsui.Layers.MemoryLayer layer = Assert.Single(control.Map.Layers.OfType<Mapsui.Layers.MemoryLayer>(),
            candidate => candidate.Name == "Вершины маршрута");
        static double[] Thresholds(Mapsui.Layers.MemoryLayer layer) => layer.Features
            .Select(feature => Assert.Single(feature.Styles.OfType<Mapsui.Styles.SymbolStyle>()).MaxVisible).ToArray();
        adapter.SetRouteVertices([parent, first, second]);
        double[] thresholds = Thresholds(layer);
        Assert.Equal(2, thresholds.Length);
        Assert.Equal(thresholds[0], thresholds[1]);
        Assert.True(thresholds[0] < double.MaxValue);
        adapter.SetRouteVertices([parent, first with { IsSelected = true }, second]);
        Assert.All(Thresholds(layer), threshold => Assert.Equal(double.MaxValue, threshold));
        adapter.SetRouteVertices([parent, first, second], showAll: true);
        Assert.All(Thresholds(layer), threshold => Assert.Equal(double.MaxValue, threshold));
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
    public void ListedBridgeTransitionsAndTurnsRenderWithoutDuplicatingUserWaypoints()
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
        Assert.Collection(layer.Features,
            feature => Assert.Equal("1", feature["vertex_label"]),
            feature => Assert.Equal("3", feature["vertex_label"]));
        Assert.All(layer.Features, feature =>
        {
            Mapsui.Styles.LabelStyle label = Assert.Single(
                Assert.IsType<Mapsui.Nts.GeometryFeature>(feature)
                    .Styles.OfType<Mapsui.Styles.LabelStyle>());
            Assert.Equal(feature["vertex_label"]?.ToString(), label.GetLabelText(feature));
            Assert.Equal(Mapsui.Styles.Color.White, label.ForeColor);
            Assert.Null(label.BackColor);
            Assert.False(label.CollisionDetection);
            Assert.Equal(0, label.Offset.X);
            Assert.Equal(0, label.Offset.Y);
            Mapsui.Styles.SymbolStyle circle = Assert.Single(feature.Styles.OfType<Mapsui.Styles.SymbolStyle>());
            Assert.Equal(Mapsui.Styles.SymbolType.Ellipse, circle.SymbolType);
            Assert.NotNull(circle.Fill);
            Assert.NotEqual(label.ForeColor, circle.Fill.Color);
        });
    }

    [AvaloniaFact]
    public void ListedStraightBridgeTransitionCanBeSelectedOnMap()
    {
        GeoCoordinate coordinate = TestData.P(100, 150);
        var control = new Mapsui.UI.Avalonia.MapControl();
        var window = new Window { Content = control, Width = 900, Height = 600 };
        window.Show();
        using var adapter = new MapsuiMapAdapter(control, new(27, 54, 27.006, 54.006));
        adapter.SetRouteVertices(
            [new RouteVertexRow(12, coordinate, RouteVertexKind.Transition)]);
        adapter.InitializeViewport();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Mapsui.Layers.MemoryLayer layer = Assert.Single(control.Map.Layers.OfType<Mapsui.Layers.MemoryLayer>(),
            candidate => candidate.Name == "Вершины маршрута");
        Mapsui.IFeature feature = Assert.Single(layer.Features);
        Assert.Equal("12", feature["vertex_label"]);
        int selected = 0;
        adapter.RouteVertexSelected += number => selected = number;
        (double worldX, double worldY) = SphericalMercator.FromLonLat(
            coordinate.Longitude, coordinate.Latitude);
        MPoint screen = control.Map.Navigator.Viewport.WorldToScreen(worldX, worldY);

        Click(window, new Avalonia.Point(screen.X, screen.Y));

        Assert.Equal(12, selected);
        window.Close();
    }

    [AvaloniaFact]
    public async Task BridgeTransitionAtCableBendIsVisibleAndSelectableWithItsListNumber()
    {
        GeoCoordinate start = TestData.P(50, 150);
        GeoCoordinate bend = TestData.P(120, 200);
        GeoCoordinate end = TestData.P(260, 150);
        using MapViewModel vm = TestData.CreateMapViewModel(new NavigationHost(),
            new PlanningSession(new BridgeBendPlanner(bend), TestData.AllowAllValidator),
            new MapAreaLoader(new MemoryData()), new MemorySettings(), new MemoryClipboard());
        await vm.AddPoint.Execute(start);
        await vm.AddPoint.Execute(end);
        await vm.BuildRoutes.Execute();

        RouteVertexRow listedTurn = Assert.Single(vm.RouteVertices,
            vertex => vertex.Kind == RouteVertexKind.Turn);
        Assert.Equal(bend, listedTurn.Coordinate);

        var control = new Mapsui.UI.Avalonia.MapControl();
        var window = new Window { Content = control, Width = 900, Height = 600 };
        window.Show();
        using var adapter = new MapsuiMapAdapter(control, new(27, 54, 27.006, 54.006));
        adapter.SetPoints(vm.Points.ToArray());
        adapter.SetRouteVertices(vm.RouteVertices.ToArray());
        adapter.InitializeViewport();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Mapsui.Layers.MemoryLayer layer = Assert.Single(control.Map.Layers.OfType<Mapsui.Layers.MemoryLayer>(),
            candidate => candidate.Name == "Вершины маршрута");
        Assert.Single(layer.Features);
        int selectedNumber = 0;
        adapter.RouteVertexSelected += number => selectedNumber = number;
        (double worldX, double worldY) = SphericalMercator.FromLonLat(bend.Longitude, bend.Latitude);
        MPoint screen = control.Map.Navigator.Viewport.WorldToScreen(worldX, worldY);

        Click(window, new Avalonia.Point(screen.X, screen.Y));

        Assert.Equal(listedTurn.Number, selectedNumber);
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

    [AvaloniaFact]
    public void CloselySpacedIntermediateMarkersAppearAfterZoomingIn()
    {
        GeoCoordinate first = TestData.P(100, 150);
        GeoCoordinate second = TestData.P(108, 150);
        var control = new Mapsui.UI.Avalonia.MapControl();
        var window = new Window { Content = control, Width = 900, Height = 600 };
        window.Show();
        using var adapter = new MapsuiMapAdapter(control, new(27, 54, 27.006, 54.006));
        adapter.SetRouteVertices(
        [
            new RouteVertexRow(1, first, RouteVertexKind.Turn, LegIndex: 1, LegSubIndex: 1),
            new RouteVertexRow(2, second, RouteVertexKind.Turn, LegIndex: 1, LegSubIndex: 2)
        ]);
        adapter.InitializeViewport();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        (double x, double y) = SphericalMercator.FromLonLat(second.Longitude, second.Latitude);
        control.Map.Navigator.CenterOnAndZoomTo(new MPoint(x, y), 1);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        int selected = 0;
        adapter.RouteVertexSelected += number => selected = number;
        MPoint screen = control.Map.Navigator.Viewport.WorldToScreen(x, y);

        Click(window, new Avalonia.Point(screen.X, screen.Y));

        Assert.Equal(0, selected);
        control.Map.Navigator.CenterOnAndZoomTo(new MPoint(x, y), 0.1);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        screen = control.Map.Navigator.Viewport.WorldToScreen(x, y);
        Click(window, new Avalonia.Point(screen.X, screen.Y));
        Assert.Equal(2, selected);
        window.Close();
    }

    [AvaloniaFact]
    public void PrimaryListSelectionHighlightsWaypointAndUsesDifferentColourForGeneratedVertices()
    {
        var control = new Mapsui.UI.Avalonia.MapControl();
        using var adapter = new MapsuiMapAdapter(control, new(27, 54, 27.006, 54.006));
        var point = new WaypointRow(Guid.NewGuid(), 1, TestData.P(100, 150));
        adapter.SetPoints([point]);
        RouteVertexRow primary = new(1, point.Coordinate, RouteVertexKind.UserWaypoint, LegIndex: 1);
        RouteVertexRow child = new(2, TestData.P(110, 150), LegIndex: 1, LegSubIndex: 1);
        adapter.SetRouteVertices([primary, child]);
        Mapsui.Layers.MemoryLayer points = Assert.Single(control.Map.Layers.OfType<Mapsui.Layers.MemoryLayer>(),
            layer => layer.Name == "Точки");
        Mapsui.Layers.MemoryLayer vertices = Assert.Single(control.Map.Layers.OfType<Mapsui.Layers.MemoryLayer>(),
            layer => layer.Name == "Вершины маршрута");
        static Mapsui.Styles.Color Colour(Mapsui.Layers.MemoryLayer layer) =>
            Assert.IsType<Mapsui.Styles.Color>(Assert.Single(Assert.Single(layer.Features).Styles.OfType<Mapsui.Styles.SymbolStyle>()).Fill!.Color);
        Mapsui.Styles.Color normal = Colour(points);
        Assert.NotEqual(normal, Colour(vertices));
        adapter.SetRouteVertices([primary with { IsSelected = true }, child]);
        Assert.NotEqual(normal, Colour(points));
        adapter.SetRouteVertices([primary, child with { IsSelected = true }]);
        Assert.Equal(normal, Colour(points));
        Assert.Equal(double.MaxValue,
            Assert.Single(Assert.Single(vertices.Features).Styles.OfType<Mapsui.Styles.SymbolStyle>()).MaxVisible);
    }

    private sealed class BridgeBendPlanner(GeoCoordinate bend) : IRoutePlanner
    {
        public Task<IReadOnlyList<RouteAlternative>> PlanAsync(
            IReadOnlyList<GeoCoordinate> points, RoutingOptions options, CancellationToken cancellationToken)
        {
            var metrics = new RouteMetrics(250, 0, 250, 0, 0, 0, 250, null);
            return Task.FromResult<IReadOnlyList<RouteAlternative>>(
            [
                new(Guid.NewGuid(), "Через поворот моста",
                    [points[0], bend, points[1]], metrics, [bend])
            ]);
        }
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
