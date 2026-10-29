using Avalonia.Headless.XUnit;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.UI.MapAdapters;
using Mapsui;
using Mapsui.Projections;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Rendering.Skia;
using Mapsui.Styles;
using Mapsui.Tiling.Layers;
using Mapsui.Tiling.Rendering;
using System.Collections.Concurrent;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using SkiaSharp;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class OfflineBasemapTests
{
    [AvaloniaFact]
    public async Task BasemapFetchesCurrentAndSingleParentResolution()
    {
        var repository = new ResolutionRepository();
        var control = new Mapsui.UI.Avalonia.MapControl();
        using var adapter = new MapsuiMapAdapter(control, repository.Info.Bounds, repository);
        RasterizingTileLayer tiled = Assert.Single(control.Map.Layers.OfType<RasterizingTileLayer>());
        double resolution = tiled.TileSource.Schema.Resolutions[10].UnitsPerPixel;
        (double x, double y) = SphericalMercator.FromLonLat(27.003, 54.003);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tiled.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(tiled.Busy) && !tiled.Busy)
            {
                finished.TrySetResult();
            }
        };

        tiled.RefreshData(new FetchInfo(new MSection(new MRect(x, y, x + 256 * resolution, y + 256 * resolution), resolution)));
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));

        double[] fetched = repository.Resolutions.Distinct().Order().ToArray();
        Assert.Equal(new[] { resolution, resolution * 2 }, fetched);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RasterTileHasOpaqueBackgroundAndPreservesTerrain(bool hasForest)
    {
        var schema = new BruTile.Predefined.GlobalSphericalMercator();
        (double x, double y) = SphericalMercator.FromLonLat(27.003, 54.003);
        BruTile.TileInfo tile = Assert.Single(schema.GetTileInfos(new BruTile.Extent(x, y, x + 1, y + 1), 15));
        BruTile.Extent extent = tile.Extent;
        MapFeature[] features = hasForest ? [ForestInside(extent)] : [];

        // Use the real production provider and tile renderer with controlled map data.
        var populatedControl = new Mapsui.UI.Avalonia.MapControl();
        using var populatedAdapter = new MapsuiMapAdapter(populatedControl, new(27, 54, 27.006, 54.006), new MemoryData(features));
        RasterizingTileLayer populated = Assert.Single(populatedControl.Map.Layers.OfType<RasterizingTileLayer>());
        byte[]? bytes = await populated.TileSource.GetTileAsync(tile);

        Assert.NotNull(bytes);
        using SKBitmap bitmap = SKBitmap.Decode(bytes);
        Assert.Equal(new SKColor(244, 242, 233), bitmap.GetPixel(0, 0));
        Assert.Equal(new SKColor(244, 242, 233), bitmap.GetPixel(255, 255));
        Assert.Equal(hasForest ? new SKColor(188, 214, 176) : new SKColor(244, 242, 233), bitmap.GetPixel(128, 128));
    }

    [AvaloniaFact]
    public async Task DetailTileHidesOldLabelsWhileMissingNeighbourRetainsParentFallback()
    {
        var schema = new BruTile.Predefined.GlobalSphericalMercator();
        (double x, double y) = SphericalMercator.FromLonLat(27.003, 54.003);
        var point = new BruTile.Extent(x, y, x + 1, y + 1);
        BruTile.TileInfo child = Assert.Single(schema.GetTileInfos(point, 15));
        BruTile.TileInfo parent = Assert.Single(schema.GetTileInfos(point, 14));
        var control = new Mapsui.UI.Avalonia.MapControl();
        using var adapter = new MapsuiMapAdapter(control, new(27, 54, 27.006, 54.006), new MemoryData());
        RasterizingTileLayer tiled = Assert.Single(control.Map.Layers.OfType<RasterizingTileLayer>());
        byte[] childImage = Assert.IsType<byte[]>(await tiled.TileSource.GetTileAsync(child));
        using var oldImage = new SKBitmap(256, 256);
        using (var canvas = new SKCanvas(oldImage))
        {
            canvas.Clear(SKColors.Red);
        }
        using SKData encodedParent = oldImage.Encode(SKEncodedImageFormat.Png, 100);
        static MRect Rect(BruTile.Extent extent) => new(extent.MinX, extent.MinY, extent.MaxX, extent.MaxY);
        using var cache = new BruTile.Cache.MemoryCache<IFeature?>(2, 10);
        cache.Add(parent.Index, new RasterFeature(new MRaster(encodedParent.ToArray(), Rect(parent.Extent))));
        cache.Add(child.Index, new RasterFeature(new MRaster(childImage, Rect(child.Extent))));
        double resolution = schema.Resolutions[15].UnitsPerPixel;
        var strategy = new TilingRenderFetchStrategy(null);
        IList<IFeature> visible = strategy.Get(Rect(parent.Extent), resolution, schema, cache);
        Assert.Equal(2, visible.Count);
        using var layer = new MemoryLayer { Features = visible, Style = new RasterStyle() };
        var viewport = new Viewport(parent.Extent.CenterX, parent.Extent.CenterY, resolution, 0, 512, 512);
        using MemoryStream composed = new MapRenderer().RenderToBitmapStream(viewport, [layer]);
        using SKBitmap bitmap = SKBitmap.Decode(composed.ToArray());
        MPoint loaded = viewport.WorldToScreen(child.Extent.CenterX, child.Extent.CenterY);
        double neighbourX = child.Extent.CenterX < parent.Extent.CenterX
            ? child.Extent.CenterX + child.Extent.Width : child.Extent.CenterX - child.Extent.Width;
        MPoint pending = viewport.WorldToScreen(neighbourX, child.Extent.CenterY);

        Assert.Equal(new SKColor(244, 242, 233), bitmap.GetPixel((int)loaded.X, (int)loaded.Y));
        Assert.Equal(SKColors.Red, bitmap.GetPixel((int)pending.X, (int)pending.Y));
    }

    private static MapFeature ForestInside(BruTile.Extent extent)
    {
        static Coordinate C(double x, double y)
        {
            (double longitude, double latitude) = SphericalMercator.ToLonLat(x, y);
            return new Coordinate(longitude, latitude);
        }

        double margin = extent.Width / 4;
        Polygon polygon = new GeometryFactory().CreatePolygon([
            C(extent.MinX + margin, extent.MinY + margin),
            C(extent.MaxX - margin, extent.MinY + margin),
            C(extent.MaxX - margin, extent.MaxY - margin),
            C(extent.MinX + margin, extent.MaxY - margin),
            C(extent.MinX + margin, extent.MinY + margin)]);
        return new(1, TerrainKind.Forest, new WKBWriter().Write(polygon), "forest", null);
    }

    private sealed class ResolutionRepository : IGeodataRepository
    {
        public DataPackageInfo Info { get; } = new MemoryData().Info;
        public ConcurrentQueue<double> Resolutions { get; } = new();

        public Task<IReadOnlyList<MapFeature>> QueryAsync(GeoBounds bounds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MapFeature>>([]);

        public Task<IReadOnlyList<MapFeature>> QueryMapAsync(GeoBounds bounds, double metresPerPixel, CancellationToken cancellationToken)
        {
            Resolutions.Enqueue(metresPerPixel);
            return QueryAsync(bounds, cancellationToken);
        }
    }
}
