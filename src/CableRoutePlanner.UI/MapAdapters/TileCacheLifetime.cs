using BruTile;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Tiling.Extensions;

namespace CableRoutePlanner.UI.MapAdapters;

// A source task finishing does not mean that Mapsui finished writing its result
// to the cache. DataChanged acknowledges that final dispatcher operation.
internal sealed class TileCacheLifetime(BackgroundTileSource source)
{
    private readonly object _gate = new();
    private readonly HashSet<TileIndex> _pending = [];
    private bool _closing;
    private Action? _cleanup;

    public async Task<IFeature?> FetchAsync(TileInfo tile)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            _pending.Add(tile.Index);
        }

        byte[]? bytes = await source.GetTileAsync(tile).ConfigureAwait(false);
        return bytes is null ? null : new RasterFeature(new MRaster(bytes, tile.Extent.ToMRect()));
    }

    public void Complete(TileInfo tile)
    {
        lock (_gate)
        {
            _pending.Remove(tile.Index);
            CleanupIfIdle();
        }
    }

    public void Close(Action cleanup)
    {
        lock (_gate)
        {
            _closing = true;
            _cleanup = cleanup;
            CleanupIfIdle();
        }
    }

    private void CleanupIfIdle()
    {
        if (_closing && _pending.Count == 0 && _cleanup is { } cleanup)
        {
            _cleanup = null;
            cleanup();
        }
    }
}
