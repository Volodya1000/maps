using Mapsui.Layers;
using Mapsui.Tiling.Fetcher;
using Mapsui.Tiling.Layers;
using Mapsui.Tiling.Provider;

namespace CableRoutePlanner.UI.MapAdapters;

internal sealed class OfflineRasterLayer : TileLayer, ISourceLayer
{
    private readonly BackgroundTileSource _source;
    private readonly FadingTileRenderFetchStrategy _transition;
    private int _animationPending;
    private bool _disposed;
    public ILayer SourceLayer { get; }

    public OfflineRasterLayer(ILayer source)
        : this(source, new BackgroundTileSource(new RasterizingTileProvider(source)), new FadingTileRenderFetchStrategy())
    {
    }

    private OfflineRasterLayer(ILayer source, BackgroundTileSource tiles, FadingTileRenderFetchStrategy transition)
        : base(tiles, minTiles: 96, maxTiles: 160,
            dataFetchStrategy: new DataFetchStrategy(maxLevelsUp: 1), renderFetchStrategy: transition)
    {
        SourceLayer = source;
        Name = source.Name;
        Style = null;
        _source = tiles;
        _transition = transition;
    }

    public override IEnumerable<Mapsui.IFeature> GetFeatures(Mapsui.MRect extent, double resolution)
    {
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
        if (disposing && !_disposed)
        {
            _disposed = true;
            _source.Dispose();
            _transition.Dispose();
            if (SourceLayer is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
