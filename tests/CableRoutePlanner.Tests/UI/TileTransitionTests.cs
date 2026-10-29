using BruTile;
using BruTile.Cache;
using BruTile.Predefined;
using CableRoutePlanner.UI.MapAdapters;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Projections;
using Mapsui.Extensions;
using Mapsui.Rendering.Skia;
using SkiaSharp;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class TileTransitionTests
{
    [Theory]
    [InlineData(0, 255, 0, true)]
    [InlineData(100, 128, 127, true)]
    [InlineData(200, 0, 255, false)]
    [InlineData(300, 0, 255, false)]
    public void DetailTileFadesOverParentWithoutAnAbruptReplacement(int milliseconds, int red, int blue, bool animating)
    {
        var clock = new ManualClock();
        using var strategy = new FadingTileRenderFetchStrategy(clock);
        using var cache = new MemoryCache<IFeature?>(2, 10);
        var schema = new GlobalSphericalMercator();
        (TileInfo child, TileInfo parent) = Tiles(schema);
        cache.Add(parent.Index, Raster(parent, SKColors.Red));
        cache.Add(child.Index, Raster(child, SKColors.Blue));
        MRect extent = Rect(child.Extent);
        double resolution = schema.Resolutions[15].UnitsPerPixel;
        _ = strategy.Get(extent, resolution, schema, cache);
        clock.Advance(milliseconds);
        IList<IFeature> features = strategy.Get(extent, resolution, schema, cache);
        using var layer = new MemoryLayer { Features = features, Style = null };
        var viewport = new Viewport(child.Extent.CenterX, child.Extent.CenterY, resolution, 0, 256, 256);
        using MemoryStream image = new MapRenderer().RenderToBitmapStream(viewport, [layer]);
        using SKBitmap bitmap = SKBitmap.Decode(image.ToArray());
        SKColor pixel = bitmap.GetPixel(128, 128);

        Assert.InRange((int)pixel.Red, red - 1, red + 1);
        Assert.InRange((int)pixel.Blue, blue - 1, blue + 1);
        Assert.Equal((byte)255, pixel.Alpha);
        Assert.Equal(animating, strategy.IsAnimating);
    }

    [Fact]
    public void PendingNeighbourKeepsParentAfterDetailTransitionFinishes()
    {
        var clock = new ManualClock();
        using var strategy = new FadingTileRenderFetchStrategy(clock);
        using var cache = new MemoryCache<IFeature?>(2, 10);
        var schema = new GlobalSphericalMercator();
        (TileInfo child, TileInfo parent) = Tiles(schema);
        cache.Add(parent.Index, Raster(parent, SKColors.Red));
        cache.Add(child.Index, Raster(child, SKColors.Blue));
        double resolution = schema.Resolutions[15].UnitsPerPixel;
        _ = strategy.Get(Rect(parent.Extent), resolution, schema, cache);
        clock.Advance(200);
        using var layer = new MemoryLayer { Features = strategy.Get(Rect(parent.Extent), resolution, schema, cache), Style = null };
        var viewport = new Viewport(parent.Extent.CenterX, parent.Extent.CenterY, resolution, 0, 512, 512);
        using MemoryStream image = new MapRenderer().RenderToBitmapStream(viewport, [layer]);
        using SKBitmap bitmap = SKBitmap.Decode(image.ToArray());
        MPoint detail = viewport.WorldToScreen(child.Extent.CenterX, child.Extent.CenterY);
        double neighbourX = child.Extent.CenterX < parent.Extent.CenterX
            ? child.Extent.CenterX + child.Extent.Width : child.Extent.CenterX - child.Extent.Width;
        MPoint pending = viewport.WorldToScreen(neighbourX, child.Extent.CenterY);

        Assert.Equal(SKColors.Blue, bitmap.GetPixel((int)detail.X, (int)detail.Y));
        Assert.Equal(SKColors.Red, bitmap.GetPixel((int)pending.X, (int)pending.Y));
        Assert.False(strategy.IsAnimating);
    }

    [Fact]
    public void LateParentDoesNotMakeAlreadyVisibleDetailTranslucent()
    {
        var clock = new ManualClock();
        using var strategy = new FadingTileRenderFetchStrategy(clock);
        using var cache = new MemoryCache<IFeature?>(2, 10);
        var schema = new GlobalSphericalMercator();
        (TileInfo child, TileInfo parent) = Tiles(schema);
        cache.Add(child.Index, Raster(child, SKColors.Blue));
        _ = strategy.Get(Rect(child.Extent), schema.Resolutions[15].UnitsPerPixel, schema, cache);
        clock.Advance(100);
        cache.Add(parent.Index, Raster(parent, SKColors.Red));

        IFeature feature = Assert.Single(strategy.Get(Rect(child.Extent), schema.Resolutions[15].UnitsPerPixel, schema, cache));

        Assert.Equal(1, Assert.Single(feature.Styles).Opacity);
        Assert.False(strategy.IsAnimating);
    }

    [Fact]
    public void AnimationExpiresEvenWhenNoMoreFramesAreRendered()
    {
        var clock = new ManualClock();
        using var strategy = new FadingTileRenderFetchStrategy(clock);
        using var cache = new MemoryCache<IFeature?>(2, 10);
        var schema = new GlobalSphericalMercator();
        (TileInfo child, TileInfo parent) = Tiles(schema);
        cache.Add(parent.Index, Raster(parent, SKColors.Red));
        cache.Add(child.Index, Raster(child, SKColors.Blue));
        _ = strategy.Get(Rect(child.Extent), schema.Resolutions[15].UnitsPerPixel, schema, cache);
        Assert.True(strategy.IsAnimating);

        clock.Advance(200);

        Assert.False(strategy.IsAnimating);
    }

    [Fact]
    public void TileWithoutCachedParentIsVisibleImmediatelyAndIdle()
    {
        using var strategy = new FadingTileRenderFetchStrategy();
        using var cache = new MemoryCache<IFeature?>(2, 10);
        var schema = new GlobalSphericalMercator();
        (TileInfo child, _) = Tiles(schema);
        RasterFeature original = Raster(child, SKColors.Blue);
        cache.Add(child.Index, original);

        RasterFeature displayed = Assert.IsType<RasterFeature>(Assert.Single(
            strategy.Get(Rect(child.Extent), schema.Resolutions[15].UnitsPerPixel, schema, cache)));

        Assert.Same(original.Raster, displayed.Raster);
        Assert.Equal(1, Assert.Single(displayed.Styles).Opacity);
        Assert.False(strategy.IsAnimating);
    }

    [Fact]
    public void ReplacedCacheEntryIsDisplayedAndDisposedStrategyCannotRestart()
    {
        using var strategy = new FadingTileRenderFetchStrategy();
        using var cache = new MemoryCache<IFeature?>(2, 10);
        var schema = new GlobalSphericalMercator();
        (TileInfo child, _) = Tiles(schema);
        cache.Add(child.Index, Raster(child, SKColors.Red));
        _ = strategy.Get(Rect(child.Extent), schema.Resolutions[15].UnitsPerPixel, schema, cache);
        RasterFeature replacement = Raster(child, SKColors.Blue);
        cache.Add(child.Index, replacement);

        RasterFeature displayed = Assert.IsType<RasterFeature>(Assert.Single(
            strategy.Get(Rect(child.Extent), schema.Resolutions[15].UnitsPerPixel, schema, cache)));
        Assert.Same(replacement.Raster, displayed.Raster);
        strategy.Dispose();
        Assert.False(strategy.IsAnimating);
        Assert.Throws<ObjectDisposedException>(() => strategy.Get(Rect(child.Extent), schema.Resolutions[15].UnitsPerPixel, schema, cache));
    }

    [Fact]
    public void ReturningToCachedDetailDoesNotRestartItsFade()
    {
        var clock = new ManualClock();
        using var strategy = new FadingTileRenderFetchStrategy(clock);
        using var cache = new MemoryCache<IFeature?>(2, 10);
        var schema = new GlobalSphericalMercator();
        (TileInfo child, TileInfo parent) = Tiles(schema);
        RasterFeature detail = Raster(child, SKColors.Blue);
        cache.Add(parent.Index, Raster(parent, SKColors.Red));
        cache.Add(child.Index, detail);
        MRect extent = Rect(child.Extent);
        double resolution = schema.Resolutions[15].UnitsPerPixel;
        _ = strategy.Get(extent, resolution, schema, cache);
        clock.Advance(300);
        _ = strategy.Get(extent, resolution, schema, cache);
        _ = strategy.Get(extent, schema.Resolutions[14].UnitsPerPixel, schema, cache);
        RasterFeature shown = strategy.Get(extent, resolution, schema, cache).OfType<RasterFeature>()
            .Single(feature => ReferenceEquals(feature.Raster, detail.Raster));
        Assert.Equal(1, Assert.Single(shown.Styles).Opacity);
        Assert.False(strategy.IsAnimating);
    }

    private static (TileInfo Child, TileInfo Parent) Tiles(GlobalSphericalMercator schema)
    {
        (double x, double y) = SphericalMercator.FromLonLat(27, 54);
        var point = new Extent(x, y, x + 1, y + 1);
        return (Assert.Single(schema.GetTileInfos(point, 15)), Assert.Single(schema.GetTileInfos(point, 14)));
    }

    private static MRect Rect(Extent extent) => new(extent.MinX, extent.MinY, extent.MaxX, extent.MaxY);

    private static RasterFeature Raster(TileInfo tile, SKColor color)
    {
        using var bitmap = new SKBitmap(256, 256);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(color);
        }
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return new RasterFeature(new MRaster(encoded.ToArray(), Rect(tile.Extent)));
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(int milliseconds) => _ticks += TimeSpan.FromMilliseconds(milliseconds).Ticks;
    }
}
