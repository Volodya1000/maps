using Avalonia.Headless.XUnit;
using CableRoutePlanner.Domain;
using CableRoutePlanner.UI.MapAdapters;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Styles;
using SkiaSharp;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class GasLabelRenderingTests
{
    [AvaloniaFact]
    public async Task OfflineProviderKeepsGasInRotatingOverlayAndOutOfRasterSource()
    {
        CableRoutePlanner.Application.MapFeature gas = TestData.Line(1,
            TerrainKind.GasPipelineCrossingCandidate, (100, 150), (200, 150));
        MemoryData repository = new(gas);
        using Mapsui.UI.Avalonia.MapControl control = new();
        using MapsuiMapAdapter adapter = new(control, repository.Info.Bounds, repository);
        FetchInfo fetch = new(new MSection(
            new MRect(MapsuiMapAdapter.Project(TestData.P(0, 0)).X,
                MapsuiMapAdapter.Project(TestData.P(0, 0)).Y,
                MapsuiMapAdapter.Project(TestData.P(300, 300)).X,
                MapsuiMapAdapter.Project(TestData.P(300, 300)).Y), 1));
        Mapsui.Tiling.Layers.TileLayer raster = Assert.Single(control.Map.Layers.OfType<Mapsui.Tiling.Layers.TileLayer>());
        Layer source = Assert.IsType<Layer>(((ISourceLayer)raster).SourceLayer);
        IFeature[] ground = (await source.DataSource!.GetFeaturesAsync(fetch)).ToArray();
        Assert.DoesNotContain(ground, feature => feature.Styles.Any(style => style is SymbolStyle or LabelStyle));
        Layer overlay = Assert.Single(control.Map.Layers.OfType<Layer>());
        IFeature[] annotations = await MapAnnotationRenderingTests.LoadOverlayAsync(overlay, fetch);
        IFeature annotation = Assert.Single(annotations);
        Assert.True(Assert.Single(annotation.Styles.OfType<SymbolStyle>()).RotateWithMap);
        Assert.Equal(MapsuiMapAdapter.Project(TestData.P(150, 150)).X, annotation.Extent!.Centroid.X, 5);
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    [InlineData(317)]
    public void GasSymbolRotatesWithMapAndKeepsWorldAnchor(double rotation)
    {
        CableRoutePlanner.Application.MapFeature gas = TestData.Line(1,
            TerrainKind.GasPipelineCrossingCandidate, (100, 150), (200, 150));
        IFeature annotation = Assert.Single(MapsuiMapAdapter.PrepareAnnotations([gas], default));
        SymbolStyle symbol = Assert.Single(annotation.Styles.OfType<SymbolStyle>());
        Assert.True(symbol.RotateWithMap);
        Assert.DoesNotContain(MapsuiMapAdapter.PrepareFeatures([gas], default, 1, includeAnnotations: false),
            feature => feature.Styles.Any(style => style is LabelStyle or SymbolStyle));
        MPoint anchor = annotation.Extent!.Centroid;
        using MemoryLayer layer = new() { Features = [annotation], Style = null };
        Viewport viewport = new(anchor.X - 40, anchor.Y - 30, 1, rotation, 320, 320);
        using SKBitmap image = MapAnnotationRenderingTests.Render(viewport, layer);
        MPoint screen = viewport.WorldToScreen(anchor.X, anchor.Y);
        List<(int X, int Y)> ink = [];
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                if (image.GetPixel(x, y).Alpha > 100)
                {
                    ink.Add((x, y));
                }
            }
        }
        Assert.NotEmpty(ink);
        Assert.InRange((ink.Min(p => p.X) + ink.Max(p => p.X)) / 2.0 - screen.X, -2, 2);
        Assert.InRange((ink.Min(p => p.Y) + ink.Max(p => p.Y)) / 2.0 - screen.Y, -2, 2);
        using SKBitmap reference = MapAnnotationRenderingTests.Render(viewport with { CenterX = anchor.X, CenterY = anchor.Y, Rotation = 0 }, layer);
        using SKBitmap turned = MapAnnotationRenderingTests.Render(viewport with { CenterX = anchor.X, CenterY = anchor.Y }, layer);
        if (rotation == 0)
        {
            Assert.Equal(reference.Pixels, turned.Pixels);
        }
        else
        {
            Assert.False(reference.Pixels.SequenceEqual(turned.Pixels));
        }
        if (rotation is 90 or 270)
        {
            Assert.True(ink.Max(p => p.Y) - ink.Min(p => p.Y) > ink.Max(p => p.X) - ink.Min(p => p.X));
        }
    }
}
