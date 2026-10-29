using CableRoutePlanner.Domain;

namespace CableRoutePlanner.Application;

public enum RouteCalculationOutcome { Completed, Cancelled, Failed }

public sealed record RouteCalculationResult(long RequestId, long Revision, RouteCalculationOutcome Outcome,
    IReadOnlyList<RouteAlternative> Routes, RoutingOptions? Options, string? Error);

public sealed record PointMoveResult(bool Success, PlanningException? Error);

public interface IRouteCalculationService : IDisposable
{
    bool IsBusy { get; }
    Task Completion { get; }
    bool IsCurrent(RouteCalculationResult result);
    Task<RouteCalculationResult?> BuildAsync();
    Task<PointMoveResult?> MovePointAsync(Guid id, GeoCoordinate coordinate);
    void Cancel();
    void Invalidate();
}

public sealed class RouteCalculationService(
    IPlanningSession session,
    ISettingsStore settings,
    Func<GeoBounds> bounds) : IRouteCalculationService
{
    private readonly object _gate = new();
    private CancellationTokenSource? _calculation;
    private CancellationTokenSource? _pointMove;
    private Task _completion = Task.CompletedTask;
    private long _requestId;
    private bool _disposed;

    public bool IsBusy { get { lock (_gate) { return _calculation is not null; } } }
    public Task Completion { get { lock (_gate) { return _completion; } } }

    public bool IsCurrent(RouteCalculationResult result)
    {
        lock (_gate)
        {
            return !_disposed && result.RequestId == _requestId && result.Revision == session.Revision;
        }
    }

    public async Task<RouteCalculationResult?> BuildAsync()
    {
        CancellationTokenSource current;
        TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long request;
        long revision;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CancellationTokenSource? previous = _calculation;
            current = new CancellationTokenSource();
            _calculation = current;
            _completion = finished.Task;
            request = ++_requestId;
            revision = session.Revision;
            previous?.Cancel();
        }

        RoutingOptions? options = null;
        RouteCalculationResult? Result(RouteCalculationOutcome outcome, IReadOnlyList<RouteAlternative> routes, string? error)
        {
            RouteCalculationResult result = new(request, revision, outcome, routes, options, error);
            return IsCurrent(result) ? result : null;
        }

        try
        {
            options = settings.Load();
            IReadOnlyList<RouteAlternative> routes = await session.BuildAsync(options, current.Token);
            current.Token.ThrowIfCancellationRequested();
            return Result(RouteCalculationOutcome.Completed, routes, null);
        }
        catch (OperationCanceledException)
        {
            return Result(RouteCalculationOutcome.Cancelled, [], null);
        }
        catch (Exception error)
        {
            return Result(RouteCalculationOutcome.Failed, [], error.Message);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_calculation, current))
                {
                    _calculation = null;
                }

                current.Dispose();
            }

            finished.TrySetResult();
        }
    }

    public async Task<PointMoveResult?> MovePointAsync(Guid id, GeoCoordinate coordinate)
    {
        CancellationTokenSource current;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CancellationTokenSource? previous = _pointMove;
            current = new CancellationTokenSource();
            _pointMove = current;
            previous?.Cancel();
        }

        try
        {
            if (!bounds().Contains(coordinate))
            {
                throw new PlanningException("Для этой точки нет данных в офлайн-пакете.", PlanningErrorReason.PointOutsidePackage);
            }

            await session.MoveAsync(id, coordinate, current.Token);
            current.Token.ThrowIfCancellationRequested();
            return new PointMoveResult(true, null);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            return null;
        }
        catch (PlanningException error)
        {
            return current.IsCancellationRequested ? null : new PointMoveResult(false, error);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pointMove, current))
                {
                    _pointMove = null;
                }

                current.Dispose();
            }
        }
    }

    public void Cancel()
    {
        lock (_gate)
        { _calculation?.Cancel(); }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _requestId++;
            _calculation?.Cancel();
            _pointMove?.Cancel();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            { return; }
            _disposed = true;
            _calculation?.Cancel();
            _pointMove?.Cancel();
        }
    }
}
