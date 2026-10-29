using Avalonia.Threading;
using Mapsui.Layers;
using Mapsui.Tiling.Fetcher;
using Mapsui.Tiling.Layers;
using Mapsui.Tiling.Provider;

namespace CableRoutePlanner.UI.MapAdapters;

internal sealed class OfflineRasterLayer : TileLayer, ISourceLayer
{
    private readonly BackgroundTileSource _source;
    private readonly FadingTileRenderFetchStrategy _transition;
    private readonly DispatcherTimer _animation = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Action _refreshGraphics;
    private bool _disposed;
    public ILayer SourceLayer { get; }

    public OfflineRasterLayer(ILayer source, Action refreshGraphics)
        : this(source, refreshGraphics, new BackgroundTileSource(new RasterizingTileProvider(source)), new FadingTileRenderFetchStrategy())
    {
    }

    private OfflineRasterLayer(ILayer source, Action refreshGraphics, BackgroundTileSource tiles, FadingTileRenderFetchStrategy transition)
        : base(tiles, minTiles: 96, maxTiles: 160,
            dataFetchStrategy: new DataFetchStrategy(maxLevelsUp: 1), renderFetchStrategy: transition)
    {
        SourceLayer = source;
        Name = source.Name;
        Style = null;
        _source = tiles;
        _transition = transition;
        _refreshGraphics = refreshGraphics;
        _animation.Tick += OnAnimationTick;
    }

    public override IEnumerable<Mapsui.IFeature> GetFeatures(Mapsui.MRect extent, double resolution)
    {
        IEnumerable<Mapsui.IFeature> features = base.GetFeatures(extent, resolution);
        if (_transition.IsAnimating && !_disposed)
        {
            _animation.Start();
        }
        return features;
    }

    private void OnAnimationTick(object? sender, EventArgs args)
    {
        if (_disposed)
        {
            _animation.Stop();
            return;
        }
        _refreshGraphics();
        if (!_transition.IsAnimating)
        {
            _animation.Stop();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _animation.Stop();
            _animation.Tick -= OnAnimationTick;
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
