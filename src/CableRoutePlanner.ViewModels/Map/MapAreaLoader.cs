using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;

namespace CableRoutePlanner.ViewModels;

public abstract record MapAreaLoadResult(long RequestId);

public sealed record MapAreaLoaded(long RequestId, IReadOnlyList<MapFeature> Features) : MapAreaLoadResult(RequestId);

public sealed record MapAreaFailed(long RequestId, string Message) : MapAreaLoadResult(RequestId);

public interface IMapAreaLoader
{
    DataPackageInfo Info { get; }
    bool IsCurrent(MapAreaLoadResult result);
    void Invalidate();
    Task<MapAreaLoadResult?> LoadLatestAsync(GeoBounds bounds, double metresPerPixel, CancellationToken cancellationToken);
}

public sealed class MapAreaLoader(IGeodataRepository repository) : IMapAreaLoader
{
    private long _requestId;

    public DataPackageInfo Info => repository.Info;

    public bool IsCurrent(MapAreaLoadResult result) =>
        result.RequestId == Interlocked.Read(ref _requestId);

    public void Invalidate() => Interlocked.Increment(ref _requestId);

    public async Task<MapAreaLoadResult?> LoadLatestAsync(
        GeoBounds bounds,
        double metresPerPixel,
        CancellationToken cancellationToken)
    {
        long request = Interlocked.Increment(ref _requestId);
        try
        {
            IReadOnlyList<MapFeature> features = await repository.QueryMapAsync(bounds, metresPerPixel, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return request == Interlocked.Read(ref _requestId)
                ? new MapAreaLoaded(request, features)
                : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            return request == Interlocked.Read(ref _requestId)
                ? new MapAreaFailed(request, ex.Message)
                : null;
        }
    }
}
