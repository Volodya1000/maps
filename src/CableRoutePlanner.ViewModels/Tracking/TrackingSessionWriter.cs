using CableRoutePlanner.Application;

namespace CableRoutePlanner.ViewModels;

internal sealed class TrackingSessionWriter(
    ITrackingSessionStore store,
    Action<Exception> onError) : IDisposable
{
    private readonly object _gate = new();
    private RouteProgressSnapshot? _pending;
    private Task _worker = Task.CompletedTask;
    private bool _workerActive;
    private bool _disposed;

    public void QueueSave(RouteProgressSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending = snapshot;
            if (_workerActive)
            {
                return;
            }

            _workerActive = true;
            _worker = Task.Run(Drain);
        }
    }

    public void SaveImmediately(RouteProgressSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Task worker;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            worker = _worker;
        }

        worker.GetAwaiter().GetResult();
        lock (_gate)
        {
            _pending = null;
        }

        Save(snapshot);
    }

    public void Dispose()
    {
        Task worker;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending = null;
            worker = _worker;
        }

        worker.GetAwaiter().GetResult();
    }

    private void Drain()
    {
        while (true)
        {
            RouteProgressSnapshot? snapshot;
            lock (_gate)
            {
                snapshot = _pending;
                _pending = null;
                if (snapshot is null)
                {
                    _workerActive = false;
                    return;
                }
            }

            Save(snapshot);
        }
    }

    private void Save(RouteProgressSnapshot snapshot)
    {
        try
        {
            store.Save(snapshot);
        }
        catch (Exception ex)
        {
            onError(ex);
        }
    }
}
