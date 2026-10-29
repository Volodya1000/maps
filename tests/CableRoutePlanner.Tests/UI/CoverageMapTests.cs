using Avalonia.Headless.XUnit;
using CableRoutePlanner.Application;
using CableRoutePlanner.UI.MapAdapters;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Projections;
using Mapsui.Rendering.Skia;
using Mapsui.Styles;
using SkiaSharp;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class CoverageMapTests
{
    [AvaloniaTheory]
    [InlineData(50, false, false)]
    [InlineData(175, true, false)]
    [InlineData(50, true, true)]
    [InlineData(300, false, true)]
    public void OutsideCoverageIsShadedAndExplainedWhileInsideMapRemainsVisible(double x, bool outside, bool narrowBounds)
    {
        MemoryData repository = CoverageTestData.Repository();
        if (narrowBounds)
        {
            repository = new MemoryData
            {
                Info = repository.Info with
                {
                    Bounds = new(TestData.P(250, 0).Longitude, TestData.P(250, 0).Latitude,
                    TestData.P(350, 600).Longitude, TestData.P(350, 600).Latitude)
                }
            };
        }
        var control = new Mapsui.UI.Avalonia.MapControl();
        using var adapter = new MapsuiMapAdapter(control, repository.Info.Bounds, repository);
        ILayer coverage = Assert.Single(control.Map.Layers, layer => layer.Name == "Граница офлайн-пакета");
        (double cx, double cy) = SphericalMercator.FromLonLat(TestData.P(x, 500).Longitude, TestData.P(x, 500).Latitude);
        var viewport = new Viewport(cx, cy, 1, 0, 128, 128);
        IFeature[] features = coverage.GetFeatures(new MRect(cx - 64, cy - 64, cx + 64, cy + 64), 1).ToArray();
        IFeature[] notices = features.Where(feature => feature.Styles.OfType<LabelStyle>().Any()).ToArray();
        Assert.Equal(outside ? 1 : 0, notices.Length);
        if (outside)
        {
            Assert.Equal("Нет данных офлайн-пакета", Assert.Single(notices[0].Styles.OfType<LabelStyle>()).GetLabelText(notices[0]));
            Assert.Equal(cx, notices[0].Extent!.Centroid.X, 6);
            Assert.Equal(cy, notices[0].Extent!.Centroid.Y, 6);
        }
        using var mask = new MemoryLayer { Features = features.Except(notices), Style = null };
        using MemoryStream png = new MapRenderer().RenderToBitmapStream(viewport, [mask], Color.Lime);
        using SKBitmap bitmap = SKBitmap.Decode(png.ToArray());

        Assert.Equal(outside ? new SKColor(224, 228, 231) : SKColors.Lime, bitmap.GetPixel(64, 64));
    }
}
