using Avalonia.Headless.XUnit;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.UI.MapAdapters;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Styles;
using SkiaSharp;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class MapAnnotationBehaviorTests
{
    [AvaloniaTheory]
    [InlineData(1.99, 2, 2)]
    [InlineData(2, 2, 2)]
    [InlineData(2.01, 1, 2)]
    [InlineData(4.99, 1, 2)]
    [InlineData(5, 1, 2)]
    [InlineData(5.01, 0, 0)]
    public void CachedAnnotationsRespectBothVisibilityBoundariesInActualPixels(double resolution, int labels, int signs)
    {
        MapFeature bridge = TestData.Line(1, TerrainKind.BridgeCandidate, (70, 50), (230, 50));
        MapFeature tunnel = Tunnel(2, 100, 300, 200, 300);
        using MapAnnotationLayer layer = new();
        layer.SetFeatures(MapsuiMapAdapter.PrepareAnnotations([bridge, tunnel], default));
        IFeature[] features = layer.GetFeatures(new MRect(0, 0, 1, 1), resolution).ToArray();
        Assert.Equal(labels, features.Count(feature => feature.Styles.OfType<LabelStyle>().Any() && HasInk(feature, resolution)));
        Assert.Equal(signs, features.Count(feature => feature.Styles.OfType<SymbolStyle>().Any() && HasInk(feature, resolution)));
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    [InlineData(317)]
    public void CollisionSelectionRestoresTextOnZoomAndKeepsEveryEntrance(double rotation)
    {
        MapFeature bridge = TestData.Line(1, TerrainKind.BridgeCandidate, (140, 150), (160, 150));
        MapFeature tunnel = Tunnel(2, 149, 150, 151, 150);
        using MapAnnotationLayer layer = new();
        layer.SetFeatures(MapsuiMapAdapter.PrepareAnnotations([bridge, tunnel, bridge, tunnel], default));
        IFeature[] close = layer.GetFeatures(new MRect(0, 0, 1, 1), 1).ToArray();
        Assert.Equal("Подземный переход", Assert.Single(LabelTexts(close)));
        Assert.Equal(2, close.Count(feature => feature.Styles.OfType<SymbolStyle>().Any()));
        foreach (IFeature feature in close.Where(feature => feature.Styles.OfType<LabelStyle>().Any()))
        {
            Assert.True(HasInk(feature, 1, rotation));
        }
        IFeature[] middle = layer.GetFeatures(new MRect(0, 0, 1, 1), 2.01).ToArray();
        Assert.Equal("Мост", Assert.Single(LabelTexts(middle.Where(feature => HasInk(feature, 2.01, rotation)))));
        IFeature[] zoomed = layer.GetFeatures(new MRect(0, 0, 1, 1), 0.01).ToArray();
        Assert.Equal(2, zoomed.Count(feature => feature.Styles.OfType<LabelStyle>().Any() && HasInk(feature, 0.01, rotation)));
        Assert.Single(layer.GetFeatures(new MRect(0, 0, 1, 1), 1)
            .SelectMany(feature => feature.Styles.OfType<LabelStyle>()));
    }

    [AvaloniaFact]
    public async Task DirectAdapterRetainsAnnotationsAcrossZoomWithoutReplacingSourceData()
    {
        using Mapsui.UI.Avalonia.MapControl control = new();
        using MapsuiMapAdapter adapter = new(control, new(27, 54, 27.006, 54.006));
        control.Map.Navigator.SetViewport(new Viewport(0, 0, 5.01, 0, 640, 640), 0);
        MapFeature bridge = TestData.Line(1, TerrainKind.BridgeCandidate, (70, 50), (230, 50));
        MapFeature tunnel = Tunnel(2, 100, 300, 200, 300);
        await adapter.SetFeaturesAsync([bridge, tunnel]);
        MapAnnotationLayer layer = Assert.Single(control.Map.Layers.OfType<MapAnnotationLayer>());
        Assert.DoesNotContain(layer.GetFeatures(new MRect(0, 0, 1, 1), 5.01), feature => HasInk(feature, 5.01));
        Assert.Equal(2, layer.GetFeatures(new MRect(0, 0, 1, 1), 1)
            .Count(feature => feature.Styles.OfType<LabelStyle>().Any() && HasInk(feature, 1)));
        MapFeature moved = TestData.Line(1, TerrainKind.BridgeCandidate, (370, 50), (530, 50));
        await adapter.SetFeaturesAsync([moved, tunnel]);
        IFeature movedLabel = Assert.Single(layer.GetFeatures(new MRect(0, 0, 1, 1), 1),
            feature => feature.Styles.OfType<LabelStyle>().Any(label => label.GetLabelText(feature) == "Мост"));
        NetTopologySuite.Geometries.Coordinate expected = MapsuiMapAdapter.Project(TestData.P(450, 50));
        Assert.Equal(expected.X, movedLabel.Extent!.Centroid.X, 8);
        Assert.Equal(expected.Y, movedLabel.Extent.Centroid.Y, 8);
    }

    [AvaloniaFact]
    public async Task ShuffledAndPartialNetworkFetchKeepsOneOriginalWorldAnchor()
    {
        MapFeature first = TestData.Line(10, TerrainKind.PedestrianUnderpassCandidate, (100, 100), (150, 100)) with
        { UnderpassTopology = new(1, [20, 11], [20]) };
        MapFeature second = TestData.Line(11, TerrainKind.PedestrianUnderpassCandidate, (150, 100), (200, 100)) with
        { UnderpassTopology = new(1, [11, 21], [21]) };
        using MapAnnotationLayer layer = new();
        NetTopologySuite.Geometries.Coordinate expected = MapsuiMapAdapter.Project(TestData.P(100, 100));
        FetchInfo fetch = new(new MSection(new MRect(expected.X - 1000, expected.Y - 1000, expected.X + 1000, expected.Y + 1000), 1));
        layer.DataSource = new Mapsui.Providers.MemoryProvider(MapsuiMapAdapter.PrepareAnnotations([second, first, first], default));
        IFeature initial = Assert.Single(await MapAnnotationRenderingTests.LoadOverlayAsync(layer, fetch),
            feature => feature.Styles.OfType<LabelStyle>().Any());
        Assert.Equal(expected.X, initial.Extent!.Centroid.X, 8);
        Assert.Equal(expected.Y, initial.Extent.Centroid.Y, 8);
        layer.DataSource = new Mapsui.Providers.MemoryProvider(MapsuiMapAdapter.PrepareAnnotations([second], default));
        IFeature partial = Assert.Single(await MapAnnotationRenderingTests.LoadOverlayAsync(layer, fetch),
            feature => feature.Styles.OfType<LabelStyle>().Any());
        Assert.Equal(initial.Extent, partial.Extent);
        Assert.True(HasInk(partial, 1));
        layer.DataSource = new Mapsui.Providers.MemoryProvider(MapsuiMapAdapter.PrepareAnnotations([first, second], default));
        IFeature restored = Assert.Single(await MapAnnotationRenderingTests.LoadOverlayAsync(layer, fetch),
            feature => feature.Styles.OfType<LabelStyle>().Any());
        Assert.Equal(initial.Extent, restored.Extent);
    }

    private static MapFeature Tunnel(long id, double x1, double y1, double x2, double y2) =>
        TestData.Line(id, TerrainKind.PedestrianUnderpassCandidate, (x1, y1), (x2, y2)) with
        { UnderpassTopology = new(id, [10, 11], [10, 11]) };

    private static IEnumerable<string?> LabelTexts(IEnumerable<IFeature> features) =>
        features.SelectMany(feature => feature.Styles.OfType<LabelStyle>().Select(label => label.GetLabelText(feature)));

    private static bool HasInk(IFeature feature, double resolution, double rotation = 0)
    {
        MPoint anchor = feature.Extent!.Centroid;
        using MemoryLayer layer = new() { Features = [feature], Style = null };
        using SKBitmap bitmap = MapAnnotationRenderingTests.Render(new Viewport(anchor.X, anchor.Y, resolution, rotation, 320, 160), layer);
        return bitmap.Pixels.Any(pixel => pixel.Alpha > 0);
    }
}
