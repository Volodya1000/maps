using Avalonia.Headless.XUnit;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.UI.MapAdapters;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Rendering.Skia;
using SkiaSharp;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class BridgeRenderingTests
{
    private static readonly System.Text.Json.JsonSerializerOptions FixtureJson = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(7)]
    public void ProletarskayaFootbridgeIsReadableWithoutBuildingARoute(double resolution)
    {
        NetTopologySuite.Geometries.LineString line = new(
        [
            new(27.585844, 53.8913863), new(27.5858614, 53.8913736),
            new(27.5863783, 53.8910639), new(27.5866182, 53.8909108)
        ]);
        MapFeature bridge = new(59328, TerrainKind.BridgeCandidate,
            new NetTopologySuite.IO.WKBWriter().Write(line), "way/25381163");
        NetTopologySuite.Geometries.Coordinate center = MapsuiMapAdapter.Project(new(53.8912, 27.5861517));
        using MemoryLayer layer = new() { Features = MapsuiMapAdapter.PrepareFeatures([bridge], default, resolution), Style = null };
        using MemoryStream png = new MapRenderer().RenderToBitmapStream(new Viewport(center.X, center.Y, resolution, 0, 256, 256), [layer], MapsuiStyleFactory.MapBackground);
        using SKBitmap bitmap = SKBitmap.Decode(png.ToArray());
        MapFeature[] context = System.Text.Json.JsonSerializer.Deserialize<MapFeature[]>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Underpasses", "proletarskaya.json")), FixtureJson)!;
        Assert.Contains(context, feature => feature.Id == bridge.Id && feature.Wkb.SequenceEqual(bridge.Wkb));
        if (Environment.GetEnvironmentVariable("UNDERPASS_EVIDENCE_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, $"bridge-{resolution}.png"), png.ToArray());
            using MemoryLayer map = new() { Features = MapsuiMapAdapter.PrepareFeatures(context, default, resolution), Style = null };
            using MemoryStream mapPng = new MapRenderer().RenderToBitmapStream(new Viewport(center.X, center.Y, resolution, 0, 512, 720), [map], MapsuiStyleFactory.MapBackground);
            File.WriteAllBytes(Path.Combine(directory, $"bridge-map-{resolution}.png"), mapPng.ToArray());
        }
        // The bridge must have a contrasting deck at the default 7 m viewport
        // as well as at close zoom, independently of route overlays.
        Assert.Contains(bitmap.Pixels, pixel => pixel.Red < 110 && pixel.Green < 130 && pixel.Blue < 140);
        Assert.Contains(layer.Features, feature => feature.Styles.OfType<Mapsui.Styles.LabelStyle>()
            .Any(label => label.GetLabelText(feature) == "Мост"));
    }
}
