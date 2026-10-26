using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CableRoutePlanner.UI.MapAdapters;
using Mapsui;
using Mapsui.Projections;
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
    public void PanningStopsAtPackageEdgeWithoutResettingZoom(int horizontal, int vertical)
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
        Assert.InRange(viewport.CenterX - viewport.Width * resolution / 2, left - 0.001, right);
        Assert.InRange(viewport.CenterX + viewport.Width * resolution / 2, left, right + 0.001);
        Assert.InRange(viewport.CenterY - viewport.Height * resolution / 2, bottom - 0.001, top);
        Assert.InRange(viewport.CenterY + viewport.Height * resolution / 2, bottom, top + 0.001);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void RotatedViewportRemainsInsideOfflineExtent(double angle)
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

        Assert.InRange(limited.CenterX - halfWidth, extent.MinX - 1e-6, extent.MaxX);
        Assert.InRange(limited.CenterX + halfWidth, extent.MinX, extent.MaxX + 1e-6);
        Assert.InRange(limited.CenterY - halfHeight, extent.MinY - 1e-6, extent.MaxY);
        Assert.InRange(limited.CenterY + halfHeight, extent.MinY, extent.MaxY + 1e-6);
        Assert.Equal(angle, limited.Rotation);
        Assert.Equal(requested.Resolution, limited.Resolution);
        window.Close();
    }

    [AvaloniaFact]
    public async Task CompassResetReturnsNorthAndPreservesCameraCenterAndScale()
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
        control.Map.Navigator.RotateTo(45, 0);
        var before = control.Map.Navigator.Viewport;

        adapter.ResetNorth();
        await Task.Delay(450);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var after = control.Map.Navigator.Viewport;

        Assert.Equal(0, after.Rotation, 6);
        Assert.Equal(before.CenterX, after.CenterX, 6);
        Assert.Equal(before.CenterY, after.CenterY, 6);
        Assert.Equal(before.Resolution, after.Resolution, 6);
        window.Close();
    }
}
