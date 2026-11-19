using Avalonia.Headless.XUnit;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.UI.MapAdapters;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Rendering.Skia;
using Mapsui.Styles;
using SkiaSharp;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class UnderpassRenderingTests
{
    [AvaloniaFact]
    public void RectangularNetworkRendersFourEntrancesWithoutDiagonalShortcuts()
    {
        MapFeature ring = TestData.Line(1, TerrainKind.PedestrianUnderpassCandidate,
            (100, 100), (200, 100), (200, 200), (100, 200), (100, 100)) with
        { UnderpassTopology = new(1, [10, 11, 12, 13, 10], []) };
        MapFeature[] input =
        [
            ring,
            TestData.Line(2, TerrainKind.PedestrianUnderpassCandidate, (70, 100), (100, 100)) with { UnderpassTopology = new(1, [20, 10], [20]) },
            TestData.Line(3, TerrainKind.PedestrianUnderpassCandidate, (230, 100), (200, 100)) with { UnderpassTopology = new(1, [21, 11], [21]) },
            TestData.Line(4, TerrainKind.PedestrianUnderpassCandidate, (230, 200), (200, 200)) with { UnderpassTopology = new(1, [22, 12], [22]) },
            TestData.Line(5, TerrainKind.PedestrianUnderpassCandidate, (70, 200), (100, 200)) with { UnderpassTopology = new(1, [23, 13], [23]) }
        ];
        IFeature[] features = MapsuiMapAdapter.PrepareFeatures(input, default, 1);
        Assert.Equal(4, features.Count(feature => feature.Styles.OfType<SymbolStyle>().Any()));
        Assert.Single(features.SelectMany(feature => feature.Styles.OfType<LabelStyle>()));
        GeometryFeature[] lines = features.OfType<GeometryFeature>().Where(feature => !feature.Styles.OfType<SymbolStyle>().Any()).ToArray();
        Assert.Equal(5, lines.Length);
        Assert.Equal(5, lines[0].Geometry!.Coordinates.Length);
        NetTopologySuite.Geometries.Coordinate center = MapsuiMapAdapter.Project(TestData.P(150, 150));
        using MemoryLayer layer = new() { Features = features, Style = null };
        using MemoryStream png = new MapRenderer().RenderToBitmapStream(new Viewport(center.X, center.Y, 1, 0, 420, 320), [layer], Color.White);
        SaveEvidence("four-entrance-network.png", png.ToArray());
    }

    [AvaloniaFact]
    public void VictorySquareRendersSixConfirmedSurfaceEntrancesAndOneLabel()
    {
        MapFeature[] input = UnderpassRoutingTests.VictoryFeatures();
        IFeature[] features = MapsuiMapAdapter.PrepareFeatures(input, default, 1);
        GeometryFeature[] markers = features.OfType<GeometryFeature>().Where(feature => feature.Styles.OfType<SymbolStyle>().Any()).ToArray();
        Assert.Equal(6, markers.Length);
        Assert.Single(markers.SelectMany(marker => marker.Styles.OfType<LabelStyle>()));
        NetTopologySuite.Geometries.Coordinate center = MapsuiMapAdapter.Project(new(53.90865, 27.57505));
        using MemoryLayer layer = new() { Features = features, Style = null };
        using MemoryStream png = new MapRenderer().RenderToBitmapStream(new Viewport(center.X, center.Y, 0.5, 0, 720, 720), [layer], Color.White);
        SaveEvidence("victory-map.png", png.ToArray());
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfirmedEntrancesRenderOnBothSidesOfSingleOrDividedRoad(bool divided)
    {
        MapFeature road = TestData.Line(1, TerrainKind.Road, (-100, 145), (700, 145));
        MapFeature other = TestData.Line(3, TerrainKind.Road, (-100, 155), (700, 155));
        MapFeature tunnel = TestData.Line(2, TerrainKind.PedestrianUnderpassCandidate, (160, 100), (160, 200)) with
        { UnderpassRoadIds = divided ? new long[] { 1, 3 } : new long[] { 1 }, UnderpassTopology = new(2, [10, 11], [10, 11]) };
        MapFeature[] input = divided ? [road, other, tunnel] : [road, tunnel];
        IFeature[] features = MapsuiMapAdapter.PrepareFeatures(input, default, 1);
        GeometryFeature[] markers = features.OfType<GeometryFeature>()
            .Where(feature => feature.Styles.OfType<SymbolStyle>().Any()).ToArray();
        Assert.Equal(2, markers.Length);
        Assert.Single(markers.SelectMany(marker => marker.Styles.OfType<LabelStyle>()));
        NetTopologySuite.Geometries.Coordinate center = MapsuiMapAdapter.Project(TestData.P(160, 150));
        Viewport viewport = new(center.X, center.Y, 1, 0, 360, 320);
        using MemoryLayer layer = new() { Features = features, Style = null };
        using MemoryStream png = new MapRenderer().RenderToBitmapStream(viewport, [layer], Color.White);
        using SKBitmap bitmap = SKBitmap.Decode(png.ToArray());
        foreach (GeometryFeature marker in markers)
        {
            MPoint pixel = viewport.WorldToScreen(marker.Geometry!.Coordinate.X, marker.Geometry.Coordinate.Y);
            Assert.Contains(Enumerable.Range(-8, 17), dx =>
            {
                SKColor color = bitmap.GetPixel((int)pixel.X + dx, (int)pixel.Y);
                return color.Blue > color.Red + 50;
            });
        }
        SaveEvidence(divided ? "divided-road.png" : "single-road.png", png.ToArray());
    }

    [AvaloniaTheory]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(40)]
    public void SignHasTransparentExteriorAndReadableBlueAndWhitePixels(int size)
    {
        MapFeature tunnel = TestData.Line(2, TerrainKind.PedestrianUnderpassCandidate, (160, 100), (160, 200));
        IFeature marker = Assert.Single(MapsuiMapAdapter.PrepareUnderpassMarkers([tunnel], 1, default));
        marker.Styles.Remove(Assert.Single(marker.Styles.OfType<LabelStyle>()));
        Assert.Single(marker.Styles.OfType<SymbolStyle>()).SymbolScale = size / 40d;
        NetTopologySuite.Geometries.Coordinate center = MapsuiMapAdapter.Project(TestData.P(160, 150));
        using MemoryLayer layer = new() { Features = [marker], Style = null };
        using MemoryStream png = new MapRenderer().RenderToBitmapStream(new Viewport(center.X, center.Y, 1, 0, size, size), [layer], Color.Transparent);
        using SKBitmap bitmap = SKBitmap.Decode(png.ToArray());
        Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
        Assert.Contains(bitmap.Pixels, pixel => pixel.Blue > pixel.Red + 50 && pixel.Alpha > 200);
        Assert.Contains(bitmap.Pixels, pixel => pixel.Red > 240 && pixel.Green > 240 && pixel.Blue > 240 && pixel.Alpha > 200);
        SaveEvidence($"underpass-{size}.png", png.ToArray());
    }

    private static void SaveEvidence(string name, byte[] png)
    {
        if (Environment.GetEnvironmentVariable("UNDERPASS_EVIDENCE_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, name), png);
        }
    }
}
