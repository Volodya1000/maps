using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;

namespace CableRoutePlanner.ViewModels;

internal abstract record MapAreaLoadResult(long RequestId)
{
    internal sealed record Loaded(long RequestId, IReadOnlyList<MapFeature> Features)
        : MapAreaLoadResult(RequestId);

    internal sealed record Failed(long RequestId, string Message)
        : MapAreaLoadResult(RequestId);
}

internal sealed class MapAreaLoader(IGeodataRepository repository)
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
                ? new MapAreaLoadResult.Loaded(request, features)
                : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            return request == Interlocked.Read(ref _requestId)
                ? new MapAreaLoadResult.Failed(request, ex.Message)
                : null;
        }
    }
}
