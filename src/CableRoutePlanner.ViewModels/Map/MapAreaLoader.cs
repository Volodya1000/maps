using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;

namespace CableRoutePlanner.ViewModels;

internal sealed record MapAreaLoadResult(
    long RequestId,
    IReadOnlyList<MapFeature>? Features,
    string? ErrorMessage);

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
        var request = Interlocked.Increment(ref _requestId);
        try
        {
            var features = await repository.QueryMapAsync(bounds, metresPerPixel, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return request == Interlocked.Read(ref _requestId)
                ? new MapAreaLoadResult(request, features, null)
                : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            return request == Interlocked.Read(ref _requestId)
                ? new MapAreaLoadResult(request, null, ex.Message)
                : null;
        }
    }
}
