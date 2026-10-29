using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Raw;
using CableRoutePlanner.Domain;
using CableRoutePlanner.UI.MapAdapters;
using CableRoutePlanner.ViewModels;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Limiting;
using Mapsui.Projections;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class MapMultitouchTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void PinchBeginningOnWaypointUsesLibraryGestureWithoutEditingPoints(bool previewFirst, bool rotate)
    {
        var control = new Mapsui.UI.Avalonia.MapControl();
        var window = new Window { Content = control, Width = 900, Height = 600 };
        window.Show();
        using var adapter = new MapsuiMapAdapter(control, new GeoBounds(27, 53, 28, 54));
        using var touch = new HeadlessTouch(window);
        adapter.InitializeViewport();
        HeadlessTouch.Present();
        control.Map.Navigator.Limiter = new ViewportLimiterWithoutLimits();
        control.Map.Navigator.SetViewport(control.Map.Navigator.Viewport with { Resolution = 100, Rotation = 0 }, 0);
        MPoint world = control.Map.Navigator.Viewport.ScreenToWorld(350, 300);
        (double longitude, double latitude) = SphericalMercator.ToLonLat(world.X, world.Y);
        var point = new WaypointRow(Guid.NewGuid(), 1, new GeoCoordinate(latitude, longitude));
        adapter.SetPoints([point]);
        adapter.SetAddMode(true);
        int edits = 0;
        adapter.PointAdded += _ => edits++;
        adapter.PointMoved += (_, _) => edits++;
        adapter.WaypointSelected += _ => edits++;
        touch.Send(RawPointerEventType.TouchBegin, new(350, 300), 1);
        if (previewFirst)
        {
            touch.Send(RawPointerEventType.TouchUpdate, new(340, 300), 1);
        }
        touch.Send(RawPointerEventType.TouchBegin, new(550, 300), 2);
        Mapsui.Nts.GeometryFeature marker = Assert.IsType<Mapsui.Nts.GeometryFeature>(
            Assert.Single(control.Map.Layers.OfType<Mapsui.Layers.MemoryLayer>().Single(layer => layer.Name == "Точки").Features));
        Assert.Equal(world.X, marker.Geometry!.Coordinate.X, 6);
        Assert.Equal(world.Y, marker.Geometry.Coordinate.Y, 6);
        touch.Send(RawPointerEventType.TouchUpdate, new(650, rotate ? 500 : 300), 2);
        touch.Send(RawPointerEventType.TouchUpdate, new(250, 300), 1);
        Assert.InRange(control.Map.Navigator.Viewport.Resolution, 40, 60);
        if (rotate)
        {
            Assert.True(Math.Abs(control.Map.Navigator.Viewport.Rotation) > 20);
        }
        touch.Send(RawPointerEventType.TouchEnd, new(650, 300), 2);
        touch.Send(RawPointerEventType.TouchEnd, new(250, 300), 1);
        Assert.Equal(0, edits);
        Assert.False(control.Map.Navigator.PanLock);
        Viewport beforePan = control.Map.Navigator.Viewport;
        touch.Send(RawPointerEventType.TouchBegin, new(700, 450), 3);
        touch.Send(RawPointerEventType.TouchUpdate, new(750, 450), 3);
        touch.Send(RawPointerEventType.TouchEnd, new(750, 450), 3);
        Assert.NotEqual(beforePan.CenterX, control.Map.Navigator.Viewport.CenterX);
        window.Close();
    }
}
