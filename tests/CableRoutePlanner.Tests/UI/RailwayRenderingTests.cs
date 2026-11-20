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
    [InlineData(1, 2)]
    [InlineData(20, 2)]
    [InlineData(100, 1)]
    public void TrackHasBlackBedAndDetailedWhiteDashes(double resolution, int expectedStrokes)
    {
        IStyle[] styles = MapsuiStyleFactory.ForTerrain(TerrainKind.Railway, resolution).ToArray();
        VectorStyle[] strokes = styles.OfType<VectorStyle>().ToArray();
        Assert.Equal(expectedStrokes, strokes.Length);
        Assert.Equal(Color.Black, strokes[0].Line!.Color);
        Assert.True(strokes[0].Line!.Width > MapsuiStyleFactory.ForTerrain(TerrainKind.Road, resolution)
            .OfType<VectorStyle>().Single().Line!.Width);
        if (expectedStrokes == 2)
        {
            Assert.Equal(Color.White, strokes[1].Line!.Color);
            Assert.Equal(PenStyle.ShortDash, strokes[1].Line!.PenStyle);
            Assert.True(strokes[1].Line!.Width < strokes[0].Line!.Width);
        }
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
        Assert.Equal(Color.Black, bridge[1].Line!.Color);
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
        Assert.Contains(bitmap.Pixels, pixel => pixel.Red < 30 && pixel.Green < 30 && pixel.Blue < 30 && pixel.Alpha > 200);
        Assert.Contains(bitmap.Pixels, pixel => pixel.Red > 245 && pixel.Green > 245 && pixel.Blue > 245 && pixel.Alpha > 200);
        Assert.Contains(bitmap.Pixels, pixel => pixel.Red is > 175 and < 210 && pixel.Green is > 165 and < 200);
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
