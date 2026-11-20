using Avalonia.Headless.XUnit;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.UI.MapAdapters;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Fetcher;
using Mapsui.Layers;
using Mapsui.Rendering.Skia;
using Mapsui.Styles;
using Mapsui.Tiling.Layers;
using SkiaSharp;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class MapAnnotationRenderingTests
{
    private static readonly string[] AnnotationTexts = new[] { "Мост", "Подземный переход" };
    private static readonly System.Text.Json.JsonSerializerOptions FixtureJson = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    [InlineData(317)]
    public async Task RealOfflineTilesKeepBothLabelsReadableOnProletarskayaMap(double rotation)
    {
        MapFeature[] context = System.Text.Json.JsonSerializer.Deserialize<MapFeature[]>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Underpasses", "proletarskaya.json")), FixtureJson)!;
        MemoryData repository = new(context);
        using Mapsui.UI.Avalonia.MapControl control = new();
        using MapsuiMapAdapter adapter = new(control, repository.Info.Bounds, repository);
        TileLayer raster = Assert.Single(control.Map.Layers.OfType<TileLayer>());
        NetTopologySuite.Geometries.Coordinate center = MapsuiMapAdapter.Project(new(53.8905, 27.5857));
        double resolution = raster.TileSource.Schema.Resolutions[17].UnitsPerPixel;
        // Fit both anchors at every rotation while remaining inside the underpass label's visibility limit.
        Viewport viewport = new(center.X, center.Y,
            MapsuiStyleFactory.UnderpassLabelMaxResolution - 0.01, rotation, 1120, 1120);
        MRect extent = new(center.X - 700, center.Y - 700, center.X + 700, center.Y + 700);
        List<IFeature> tiles = [];
        foreach (BruTile.TileInfo tile in raster.TileSource.Schema.GetTileInfos(
            new BruTile.Extent(extent.MinX, extent.MinY, extent.MaxX, extent.MaxY), 17))
        {
            byte[] bytes = (await raster.TileSource.GetTileAsync(tile))!;
            tiles.Add(new RasterFeature(new MRaster(bytes, new MRect(
                tile.Extent.MinX, tile.Extent.MinY, tile.Extent.MaxX, tile.Extent.MaxY))));
        }
        Layer overlay = Assert.Single(control.Map.Layers.OfType<Layer>());
        IFeature[] annotations = await LoadOverlayAsync(overlay, new FetchInfo(new MSection(extent, resolution)));
        using MemoryLayer ground = new() { Features = tiles, Style = new RasterStyle() };
        using MemoryLayer labels = new() { Features = annotations, Style = null };
        using MemoryStream png = new MapRenderer().RenderToBitmapStream(viewport, [ground, labels], MapsuiStyleFactory.MapBackground);
        SaveEvidence($"proletarskaya-{rotation}.png", png.ToArray());
        Assert.Equal(2, annotations.Sum(feature => feature.Styles.OfType<LabelStyle>().Count()));
        using SKBitmap composite = SKBitmap.Decode(png.ToArray());
        using SKBitmap terrain = Render(viewport, ground);
        SKColor[] compositePixels = composite.Pixels;
        SKColor[] terrainPixels = terrain.Pixels;
        Assert.True(terrainPixels.Any(pixel => pixel.Alpha > 200 && pixel != new SKColor(244, 242, 233)),
            "The raster baseline must contain real map geometry.");
        foreach (IFeature annotation in annotations.Where(feature => feature.Styles.OfType<LabelStyle>().Any()))
        {
            using MemoryLayer isolated = new() { Features = [annotation], Style = null };
            using SKBitmap mask = Render(viewport, isolated);
            int inkPixels = mask.Pixels.Count(pixel => pixel.Alpha > 200);
            int changedPixels = mask.Pixels.Where((pixel, index) => pixel.Alpha > 200
                && compositePixels[index] != terrainPixels[index]).Count();
            Assert.True(changedPixels > 10,
                $"The label must contribute visible pixels to the real raster/vector composite: {inkPixels} ink, {changedPixels} changed, {annotation.Extent!.Centroid}.");
        }
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    [InlineData(317)]
    public async Task BothAnnotationTypesRenderHorizontallyAndFollowTheirWorldAnchor(double rotation)
    {
        MapFeature bridge = TestData.Line(1, TerrainKind.BridgeCandidate, (80, 60), (220, 60));
        MapFeature tunnel = TestData.Line(2, TerrainKind.PedestrianUnderpassCandidate, (100, 240), (220, 240)) with
        { UnderpassTopology = new(2, [10, 11], [10, 11]) };
        MemoryData repository = new(bridge, tunnel);
        using Mapsui.UI.Avalonia.MapControl control = new();
        using MapsuiMapAdapter adapter = new(control, repository.Info.Bounds, repository);
        Layer overlay = Assert.Single(control.Map.Layers.OfType<Layer>());
        NetTopologySuite.Geometries.Coordinate center = MapsuiMapAdapter.Project(TestData.P(150, 150));
        Viewport viewport = new(center.X, center.Y, 1, rotation, 640, 640);
        FetchInfo fetch = new(new MSection(new MRect(center.X - 600, center.Y - 600, center.X + 600, center.Y + 600), 1));
        IFeature[] annotations = await LoadOverlayAsync(overlay, fetch);
        TileLayer raster = Assert.Single(control.Map.Layers.OfType<TileLayer>());
        Layer source = Assert.IsType<Layer>(((ISourceLayer)raster).SourceLayer);
        IFeature[] terrain = (await source.DataSource!.GetFeaturesAsync(fetch)).ToArray();
        using MemoryLayer ground = new() { Features = terrain, Style = null };
        using MemoryLayer labels = new() { Features = annotations, Style = null };
        using MemoryStream snapshot = new MapRenderer().RenderToBitmapStream(viewport, [ground, labels], MapsuiStyleFactory.MapBackground);
        SaveEvidence($"annotations-{rotation}.png", snapshot.ToArray());

        Assert.DoesNotContain(terrain, feature => feature.Styles.OfType<LabelStyle>()
            .Any(label => AnnotationTexts.Contains(label.GetLabelText(feature), StringComparer.Ordinal)));
        IFeature[] labelled = annotations.Where(feature => feature.Styles.OfType<LabelStyle>().Any()).ToArray();
        Assert.Equal(AnnotationTexts, labelled.SelectMany(feature =>
            feature.Styles.OfType<LabelStyle>().Select(label => label.GetLabelText(feature))).OrderBy(text => text, StringComparer.Ordinal));
        foreach (IFeature annotation in labelled)
        {
            MPoint anchor = annotation.Extent!.Centroid;
            using MemoryLayer isolated = new() { Features = [annotation], Style = null };
            Viewport centered = new(anchor.X, anchor.Y, 1, 0, 320, 160);
            using SKBitmap reference = Render(centered, isolated);
            using SKBitmap turned = Render(centered with { Rotation = rotation }, isolated);
            // Compare the actual glyphs and symbol, not rotation properties.
            Assert.Equal(reference.Pixels, turned.Pixels);
            Assert.Contains(reference.Pixels, pixel => pixel.Alpha > 0);

            using SKBitmap placed = Render(viewport, isolated);
            SKRectI originalBounds = InkBounds(reference);
            SKRectI placedBounds = InkBounds(placed);
            MPoint screen = viewport.WorldToScreen(anchor.X, anchor.Y);
            Assert.InRange(placedBounds.Left - (screen.X - 160 + originalBounds.Left), -1, 1);
            Assert.InRange(placedBounds.Top - (screen.Y - 80 + originalBounds.Top), -1, 1);
            Assert.InRange(placedBounds.Width - originalBounds.Width, -1, 1);
            Assert.InRange(placedBounds.Height - originalBounds.Height, -1, 1);
        }
    }

    internal static async Task<IFeature[]> LoadOverlayAsync(Layer overlay, FetchInfo fetch)
    {
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, DataChangedEventArgs args) => ready.TrySetResult();
        overlay.DataChanged += Completed;
        try
        {
            overlay.RefreshData(fetch);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return overlay.GetFeatures(fetch.Extent, fetch.Resolution).ToArray();
        }
        finally
        {
            overlay.DataChanged -= Completed;
        }
    }

    internal static SKBitmap Render(Viewport viewport, params ILayer[] layers)
    {
        using MemoryStream png = new MapRenderer().RenderToBitmapStream(viewport, layers, Color.Transparent);
        return SKBitmap.Decode(png.ToArray());
    }

    private static SKRectI InkBounds(SKBitmap bitmap)
    {
        List<(int X, int Y)> pixels = [];
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha > 0)
                {
                    pixels.Add((x, y));
                }
            }
        }
        Assert.NotEmpty(pixels);
        return new(pixels.Min(pixel => pixel.X), pixels.Min(pixel => pixel.Y),
            pixels.Max(pixel => pixel.X) + 1, pixels.Max(pixel => pixel.Y) + 1);
    }

    internal static void SaveEvidence(string name, byte[] png)
    {
        if (Environment.GetEnvironmentVariable("UNDERPASS_EVIDENCE_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, name), png);
        }
    }
}
