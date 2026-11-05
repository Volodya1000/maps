using BruTile;
using Mapsui.Fetcher;
using Mapsui.Layers;
using Mapsui.Tiling.Fetcher;
using Mapsui.Tiling.Layers;
using Mapsui.Tiling.Provider;

namespace CableRoutePlanner.UI.MapAdapters;

internal sealed class OfflineRasterLayer : TileLayer, ISourceLayer
{
    private readonly BackgroundTileSource _source;
    private readonly FadingTileRenderFetchStrategy _transition;
    private readonly TileCacheLifetime _cacheLifetime;
    private int _animationPending;
    private bool _disposed;
    public ILayer SourceLayer { get; }

    public OfflineRasterLayer(ILayer source)
        : this(source, new BackgroundTileSource(new RasterizingTileProvider(source)), new FadingTileRenderFetchStrategy())
    {
    }

    private OfflineRasterLayer(ILayer source, BackgroundTileSource tiles, FadingTileRenderFetchStrategy transition)
        : this(source, tiles, transition, new TileCacheLifetime(tiles))
    {
    }

    private OfflineRasterLayer(ILayer source, BackgroundTileSource tiles,
        FadingTileRenderFetchStrategy transition, TileCacheLifetime cacheLifetime)
        : base(tiles, minTiles: 96, maxTiles: 160,
            dataFetchStrategy: new DataFetchStrategy(maxLevelsUp: 1), renderFetchStrategy: transition,
            fetchTileAsFeature: cacheLifetime.FetchAsync)
    {
        SourceLayer = source;
        Name = source.Name;
        Style = null;
        _source = tiles;
        _transition = transition;
        _cacheLifetime = cacheLifetime;
        DataChanged += OnTileCompleted;
    }

    public override IEnumerable<Mapsui.IFeature> GetFeatures(Mapsui.MRect extent, double resolution)
    {
        if (_disposed)
        {
            return [];
        }
        IEnumerable<Mapsui.IFeature> features = base.GetFeatures(extent, resolution);
        if (_transition.IsAnimating && !_disposed)
        {
            Interlocked.Exchange(ref _animationPending, 1);
        }
        return features;
    }

    public override bool UpdateAnimations()
    {
        // Keep one final frame when the transition expires, so opacity reaches 1.
        if (_disposed)
        {
            return false;
        }
        return _transition.IsAnimating || Interlocked.Exchange(ref _animationPending, 0) != 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            base.Dispose(false);
            return;
        }

        if (!_disposed)
        {
            _disposed = true;
            Enabled = false;
            AbortFetch();
            _source.Dispose();
            _transition.Dispose();
            _cacheLifetime.Close(DisposeCache);
        }
    }

    private void OnTileCompleted(object sender, DataChangedEventArgs args)
    {
        if (args.Info is TileInfo tile)
        {
            _cacheLifetime.Complete(tile);
        }
    }

    private void DisposeCache()
    {
        // BruTile 5's Dispose enumerates without locking. Wait for dispatcher
        // acknowledgements, not only source completion, before disposing it.
        DataChanged -= OnTileCompleted;
        base.Dispose(true);
        SourceLayer.Dispose();
    }
}
