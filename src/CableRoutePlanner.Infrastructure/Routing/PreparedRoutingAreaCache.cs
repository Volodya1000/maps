using System.Diagnostics;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;

namespace CableRoutePlanner.Infrastructure.Routing;

internal sealed record PreparedRoutingArea(
    GeoBounds Bounds,
    double CellSize,
    LocalProjection Projection,
    TerrainEvaluator Evaluator,
    long QueryMilliseconds,
    long PreparationMilliseconds);

// One region per planner/repository lifetime. Do not retain projected geometry for a whole country.
internal sealed class PreparedRoutingAreaCache(IGeodataRepository repository)
{
    private readonly object _gate = new();
    private PreparedRoutingArea? _last;

    public async Task<PreparedRoutingArea> GetAsync(GeoBounds bounds, double cellSize, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_last is { } cached && cached.Bounds == bounds && cached.CellSize == cellSize)
            {
                return cached with
                {
                    QueryMilliseconds = 0,
                    PreparationMilliseconds = 0
                };
            }
        }

        Stopwatch timer = Stopwatch.StartNew();
        IReadOnlyList<MapFeature> features = await repository.QueryAsync(bounds, ct);
        long queryMilliseconds = timer.ElapsedMilliseconds;
        timer.Restart();
        var projection = new LocalProjection(
            new((bounds.South + bounds.North) / 2, (bounds.West + bounds.East) / 2));
        TerrainEvaluator evaluator = await Task.Run(() => new TerrainEvaluator(features, projection), ct);
        ct.ThrowIfCancellationRequested();
        var prepared = new PreparedRoutingArea(
            bounds, cellSize, projection, evaluator, queryMilliseconds, timer.ElapsedMilliseconds);
        lock (_gate)
        {
            _last = prepared;
        }

        return prepared;
    }
}
