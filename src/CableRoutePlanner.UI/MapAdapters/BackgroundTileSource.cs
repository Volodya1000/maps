using BruTile;

namespace CableRoutePlanner.UI.MapAdapters;

// Limit both geometry preparation and rasterization, including repositories whose
// async methods complete synchronously. No heavy continuation runs on the UI thread.
internal sealed class BackgroundTileSource(ITileSource source) : ITileSource, IDisposable
{
    private const int MaximumConcurrentTiles = 2;
    private readonly SemaphoreSlim _workers = new(MaximumConcurrentTiles, MaximumConcurrentTiles);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private int _users;
    private bool _disposed;
    private bool _cancellationComplete;

    public ITileSchema Schema => source.Schema;
    public string Name => source.Name;
    public Attribution Attribution => source.Attribution;

    public async Task<byte[]?> GetTileAsync(TileInfo tileInfo)
    {
        CancellationToken token;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _users++;
            token = _shutdown.Token;
        }

        bool entered = false;
        try
        {
            await _workers.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            return await Task.Run(() => source.GetTileAsync(tileInfo), token).ConfigureAwait(false);
        }
        finally
        {
            if (entered)
            {
                _workers.Release();
            }
            lock (_gate)
            {
                _users--;
                if (_disposed && _cancellationComplete && _users == 0)
                {
                    DisposeResources();
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _shutdown.Cancel();
            _cancellationComplete = true;
            if (_users == 0)
            {
                DisposeResources();
            }
        }
    }

    private void DisposeResources()
    {
        _workers.Dispose();
        _shutdown.Dispose();
    }
}
