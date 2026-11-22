using Avalonia.Headless.XUnit;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.UI.MapAdapters;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Rendering.Skia;
using Mapsui.Styles;
using SkiaSharp;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class RailwayRenderingTests
{
    [AvaloniaTheory]
    [InlineData(2.01)]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(100)]
    [InlineData(1000)]
    public void OverviewTracksAreGreyAndNoWiderThanRoads(double resolution)
    {
        foreach (TerrainKind kind in new[] { TerrainKind.Railway, TerrainKind.RailwayBridge, TerrainKind.RailwayTunnel })
        {
            VectorStyle stroke = Assert.Single(MapsuiStyleFactory.ForTerrain(kind, resolution).OfType<VectorStyle>());
            Assert.NotEqual(Color.Black, stroke.Line!.Color);
            Assert.True(stroke.Line.Width <= MapsuiStyleFactory.ForTerrain(TerrainKind.Road, resolution)
                .OfType<VectorStyle>().Single().Line!.Width);
        }
    }

    [AvaloniaTheory]
    [InlineData(0.1)]
    [InlineData(1)]
    [InlineData(2)]
    public void DetailHasBoundedDarkBedAndWhiteInserts(double resolution)
    {
        VectorStyle[] strokes = MapsuiStyleFactory.ForTerrain(TerrainKind.Railway, resolution).OfType<VectorStyle>().ToArray();
        Assert.Equal(2, strokes.Length);
        Assert.InRange(strokes[0].Line!.Width, 1, 2);
        Assert.Equal(Color.White, strokes[1].Line!.Color);
        Assert.Equal(PenStyle.ShortDash, strokes[1].Line!.PenStyle);
    }

    [AvaloniaFact]
    public void BridgeHasCasingAndTunnelUsesDottedBed()
    {
        VectorStyle[] bridge = MapsuiStyleFactory.ForTerrain(Enum.Parse<TerrainKind>("RailwayBridge"), 1)
            .OfType<VectorStyle>().ToArray();
        VectorStyle[] tunnel = MapsuiStyleFactory.ForTerrain(Enum.Parse<TerrainKind>("RailwayTunnel"), 1)
            .OfType<VectorStyle>().ToArray();

        Assert.Equal(3, bridge.Length);
        Assert.Equal(MapsuiStyleFactory.MapBackground, bridge[0].Line!.Color);
        Assert.NotEqual(MapsuiStyleFactory.MapBackground, bridge[1].Line!.Color);
        Assert.Equal(PenStyle.ShortDash, tunnel[0].Line!.PenStyle);
    }

    [AvaloniaFact]
    public void ParallelTracksAndCrossingRoadRenderWithDistinctPixels()
    {
        MapFeature[] map =
        [
            TestData.Line(1, TerrainKind.Road, (50, 150), (250, 150)),
            TestData.Line(2, TerrainKind.Railway, (140, 50), (140, 250)),
            TestData.Line(3, TerrainKind.Railway, (160, 50), (160, 250))
        ];
        IFeature[] features = MapsuiMapAdapter.PrepareFeatures(map, default, 1, includeAnnotations: false);
        using MemoryLayer layer = new() { Features = features, Style = null };
        NetTopologySuite.Geometries.Coordinate center = MapsuiMapAdapter.Project(TestData.P(150, 150));
        using MemoryStream png = new MapRenderer().RenderToBitmapStream(
            new Viewport(center.X, center.Y, 1, 0, 512, 512), [layer], MapsuiStyleFactory.MapBackground);
        using SKBitmap bitmap = SKBitmap.Decode(png.ToArray());
        if (Environment.GetEnvironmentVariable("RAILWAY_EVIDENCE_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            using FileStream image = File.Create(Path.Combine(directory, "railway-detail.png"));
            bitmap.Encode(image, SKEncodedImageFormat.Png, 100);
        }
        Assert.Contains(bitmap.Pixels, pixel => pixel.Red < 130 && pixel.Green < 130 && pixel.Blue < 130 && pixel.Alpha > 200);
        Assert.Contains(bitmap.Pixels, pixel => pixel.Red is > 150 and < 175 && Math.Abs(pixel.Red - pixel.Green) <= 2
            && Math.Abs(pixel.Green - pixel.Blue) <= 2 && pixel.Alpha > 200);
        Assert.Contains(bitmap.Pixels, pixel => pixel.Red is > 175 and < 210 && pixel.Green is > 165 and < 200);
    }

    [AvaloniaTheory]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(100)]
    public void DenseOverviewYardAndAcuteJoinsNeverProduceDarkPatches(double resolution)
    {
        List<MapFeature> yard = Enumerable.Range(0, 30).Select(index =>
            TestData.Line(index + 1, TerrainKind.Railway, (100 + index * 4, -10000),
                (100 + index * 4, 150), (100, 10000))).ToList();
        yard.Add(yard[0] with { Id = 100 });
        IFeature[] features = MapsuiMapAdapter.PrepareFeatures(yard, default, resolution, false);
        using MemoryLayer layer = new() { Features = features, Style = null };
        NetTopologySuite.Geometries.Coordinate center = MapsuiMapAdapter.Project(TestData.P(150, 150));
        using MemoryStream png = new MapRenderer().RenderToBitmapStream(
            new Viewport(center.X, center.Y, resolution, 0, 256, 256), [layer], MapsuiStyleFactory.MapBackground);
        using SKBitmap bitmap = SKBitmap.Decode(png.ToArray());
        Assert.Contains(bitmap.Pixels, pixel => pixel.Red < 230);
        Assert.All(bitmap.Pixels, pixel =>
        {
            Assert.True(pixel.Red >= 185 && pixel.Green >= 185 && pixel.Blue >= 185);
        });
    }

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(2.01)]
    [InlineData(20)]
    [InlineData(100)]
    public void DuplicateAndSplitWaysProduceSamePixelsWithoutJoinBlobs(double resolution)
    {
        MapFeature track = TestData.Line(1, TerrainKind.Railway, (150, -10000), (150, 10000));
        MapFeature[] split = [track with { Id = 2 }, track with { Id = 3 },
            TestData.Line(4, TerrainKind.Railway, (150, -10000), (150, 150)),
            TestData.Line(5, TerrainKind.Railway, (150, 150), (150, 10000))];
        NetTopologySuite.Geometries.Coordinate center = MapsuiMapAdapter.Project(TestData.P(150, 150));
        Viewport viewport = new(center.X, center.Y, resolution, 0, 256, 256);
        using MemoryLayer original = new() { Features = MapsuiMapAdapter.PrepareFeatures([track], default, resolution, false), Style = null };
        using MemoryLayer duplicated = new() { Features = MapsuiMapAdapter.PrepareFeatures(split, default, resolution, false), Style = null };
        using SKBitmap expected = MapAnnotationRenderingTests.Render(viewport, original);
        using SKBitmap actual = MapAnnotationRenderingTests.Render(viewport, duplicated);
        Assert.Equal(expected.Pixels, actual.Pixels);
        Assert.Contains(actual.Pixels, pixel => pixel.Alpha > 0);
        for (int y = 0; y < actual.Height; y++)
        {
            int width = Enumerable.Range(0, actual.Width).Count(x => actual.GetPixel(x, y).Alpha > 0);
            Assert.InRange(width, 0, resolution <= 2 ? 4 : 2);
        }
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(90)]
    [InlineData(180)]
    public void SettlementTextFollowsMapPositionAndStaysHorizontal(double rotation)
    {
        MapFeature settlement = TestData.Line(1, TerrainKind.Settlement, (150, 150), (150, 150)) with { Name = "Минск" };
        Assert.DoesNotContain(MapsuiMapAdapter.PrepareFeatures([settlement], default, includeAnnotations: false),
            feature => feature.Styles.OfType<LabelStyle>().Any());
        IFeature annotation = Assert.Single(MapsuiMapAdapter.PrepareAnnotations([settlement], default));
        Assert.Equal("Минск", Assert.Single(annotation.Styles.OfType<LabelStyle>()).GetLabelText(annotation));
        MPoint anchor = annotation.Extent!.Centroid;
        using MemoryLayer layer = new() { Features = [annotation], Style = null };
        Viewport viewport = new(anchor.X, anchor.Y, 1, 0, 320, 160);
        using SKBitmap reference = MapAnnotationRenderingTests.Render(viewport, layer);
        using SKBitmap turned = MapAnnotationRenderingTests.Render(viewport with { Rotation = rotation }, layer);
        Assert.Equal(reference.Pixels, turned.Pixels);
        int ink = reference.Pixels.Count(pixel => pixel.Alpha > 200);
        Assert.True(ink > 100, $"Settlement label produced {ink} opaque pixels.");
    }
}
