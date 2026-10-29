using System.Diagnostics;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using NetTopologySuite.Geometries;

namespace CableRoutePlanner.Infrastructure.Routing;

internal sealed record PreparedRoutingArea(
    GeoBounds Bounds,
    double CellSize,
    LocalProjection Projection,
    TerrainEvaluator Evaluator,
    long QueryMilliseconds,
    long PreparationMilliseconds,
    bool PreferGas = false);

// One region per planner/repository lifetime. Do not retain projected geometry for a whole country.
internal sealed class PreparedRoutingAreaCache(IGeodataRepository repository)
{
    private readonly object _gate = new();
    private PreparedRoutingArea? _last;

    public async Task<PreparedRoutingArea> GetAsync(GeoBounds bounds, double cellSize, CancellationToken ct, bool preferGas = false)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_last is { } cached && cached.Bounds == bounds && cached.CellSize == cellSize && cached.PreferGas == preferGas)
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
        TerrainEvaluator evaluator = await Task.Run(() =>
        {
            var factory = new GeometryFactory();
            Geometry? coverage = repository.Info.Coverage is { } outline
                ? factory.CreatePolygon(outline.Boundary.Select(projection.Forward).ToArray()) : null;
            Coordinate minimum = projection.Forward(new GeoCoordinate(bounds.South, bounds.West));
            Coordinate maximum = projection.Forward(new GeoCoordinate(bounds.North, bounds.East));
            var rectangle = new Envelope(minimum, maximum);
            // Grid edges inside a covered search rectangle need no polygon test.
            // Other geometry (for example a full bridge guide) still must be checked.
            Envelope? fullyCoveredArea = coverage?.Covers(factory.ToGeometry(rectangle)) == true ? rectangle : null;
            return new TerrainEvaluator(features, projection, preferGas: preferGas, coverage: coverage, fullyCoveredArea: fullyCoveredArea);
        }, ct);
        ct.ThrowIfCancellationRequested();
        var prepared = new PreparedRoutingArea(
            bounds, cellSize, projection, evaluator, queryMilliseconds, timer.ElapsedMilliseconds, preferGas);
        lock (_gate)
        {
            _last = prepared;
        }

        return prepared;
    }
}
