using System.Diagnostics;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using NetTopologySuite.Geometries;

namespace CableRoutePlanner.Infrastructure.Routing;

internal sealed record PreparedRoutingArea(
    GeoBounds Bounds,
    LocalProjection Projection,
    PreparedTerrainData Terrain,
    long QueryMilliseconds,
    long PreparationMilliseconds);

/// <summary>Retains one read-only spatial snapshot per planner/repository lifetime.</summary>
/// <remarks>
/// Exact bounds identify the snapshot within this immutable repository's lifetime.
/// Grid spacing and gas preference belong to each calculation, not preparation.
/// Preparation occurs outside the publication lock; concurrent misses may
/// prepare independently. Failed or cancelled preparation never replaces a valid
/// entry. Query/preparation timings are zero on reuse; search state is never shared.
/// </remarks>
internal sealed class PreparedRoutingAreaCache(IGeodataRepository repository)
{
    private readonly object _gate = new();
    private PreparedRoutingArea? _last;

    public async Task<PreparedRoutingArea> GetAsync(GeoBounds bounds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_last is { } cached && cached.Bounds == bounds)
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
        PreparedTerrainData terrain = await Task.Run(() =>
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
            return new PreparedTerrainData(features, projection, coverage, fullyCoveredArea);
        }, ct);
        ct.ThrowIfCancellationRequested();
        var prepared = new PreparedRoutingArea(
            bounds, projection, terrain, queryMilliseconds, timer.ElapsedMilliseconds);
        lock (_gate)
        {
            _last = prepared;
        }

        return prepared;
    }
}
