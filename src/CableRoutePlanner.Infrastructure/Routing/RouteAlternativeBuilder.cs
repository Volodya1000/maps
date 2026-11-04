using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using System.Collections.ObjectModel;
using NetTopologySuite.Geometries;

namespace CableRoutePlanner.Infrastructure.Routing;

/// <summary>Validates and measures completed paths, retaining distinct alternatives and all profile failures.</summary>
/// <remarks>
/// One instance belongs to one calculation. Costs are summed per segment while
/// displayed object counts are unique across the route. Profiles retain their
/// declared order; gas preference is a stable partition applied only at completion.
/// </remarks>
internal sealed class RouteAlternativeBuilder(
    TerrainEvaluator evaluator,
    RouteGeometryPostProcessor postProcessor,
    PreparedTerrainData terrain,
    RoutingOptions options)
{
    private readonly LocalProjection projection = terrain.Projection;
    private List<RouteAlternative> result = [];
    private readonly List<ProfileSearchFailure> failures = [];

    public void RecordFailure(CostProfile profile, PlanningException error) =>
        failures.Add(new(profile.Name, error));

    /// <summary>Rechecks every final segment; returns a structured length rejection or retains a distinct alternative.</summary>
    public PlanningException? Add(IReadOnlyList<Coordinate> route, CostProfile profile, CancellationToken ct)
    {
        if (route.Count < 2)
        {
            throw new PlanningException("Все точки совпадают. Установите разные точки.");
        }

        double length = 0, forest = 0, field = 0, cost = 0;
        var roads = new HashSet<long>();
        var crossings = new HashSet<long>();
        var bridgeCandidates = new HashSet<long>();
        HashSet<long> gasCandidates = [];
        for (int i = 1; i < route.Count; i++)
        {
            EdgeEvaluation edge = evaluator.Evaluate(route[i - 1], route[i])
                ?? throw new PlanningException("Маршрут не прошёл проверку препятствий.");
            length += edge.Length;
            forest += edge.Forest;
            field += edge.Field;
            cost += edge.Cost(profile);
            roads.UnionWith(edge.Roads);
            crossings.UnionWith(edge.Crossings);
            bridgeCandidates.UnionWith(edge.BridgeCandidates);
            gasCandidates.UnionWith(edge.GasCandidates);
        }

        if (length > options.MaxRouteLengthMetres)
        {
            PlanningException error = new(
                $"Маршрут длиной {length:F0} м превышает заданный максимум "
                + $"{options.MaxRouteLengthMetres:F0} м.",
                PlanningErrorReason.RouteLengthLimitExceeded)
            {
                ActualRouteLengthMetres = length,
                MaximumRouteLengthMetres = options.MaxRouteLengthMetres
            };
            return error;
        }

        // A nearby aerial candidate and a ground crossing are meaningful choices,
        // even when their lines occupy almost the same map corridor.
        if (result.Any(existing => (existing.GasPipelineCandidateIds ?? [])
            .SequenceEqual(gasCandidates.Order())
            && Similar(existing.Geometry, route, projection, options.CellSizeMetres)))
        {
            return null;
        }

        result.Add(
            new(
                Guid.NewGuid(),
                profile.Name,
                Array.AsReadOnly(route.Select(projection.Inverse).ToArray()),
                new(
                    length,
                    forest,
                    field,
                    roads.Count,
                    crossings.Count,
                    bridgeCandidates.Count,
                    cost,
                    null,
                    gasCandidates.Count),
                Array.AsReadOnly(postProcessor.BridgeTransitionPoints(route)
                    .Select(projection.Inverse).ToArray()),
                terrain.RoadContacts.Analyze(route, ct),
                Array.AsReadOnly(gasCandidates.Order().ToArray())));
        return null;
    }

    /// <summary>Returns stable alternatives, or the most specific diagnostic with every profile failure attached.</summary>
    public ReadOnlyCollection<RouteAlternative> Complete()
    {
        if (result.Count == 0)
        {
            // Prefer proof of a valid over-limit route, then a specific graph
            // connection diagnostic, then the first exhausted-search diagnostic.
            // Stable profile order breaks ties; retain every failure for consumers.
            PlanningException selected = failures.Select(failure => failure.Error)
                .OrderBy(error => error.Reason switch
                {
                    PlanningErrorReason.RouteLengthLimitExceeded => 0,
                    PlanningErrorReason.GridConnectionUnavailable => 1,
                    _ => 2
                })
                .ThenBy(error => error.ActualRouteLengthMetres ?? double.PositiveInfinity)
                .First();
            throw new PlanningException(selected.Message, selected.Reason, selected.TerrainKind, selected)
            {
                ActualRouteLengthMetres = selected.ActualRouteLengthMetres,
                MaximumRouteLengthMetres = selected.MaximumRouteLengthMetres,
                SearchFailures = failures.AsReadOnly()
            };
        }

        if (options.PreferOverheadGasCrossings)
        {
            // Consumers select the first alternative. Keep the shortest ground route
            // available, but initially show a mapped gas crossing when one was found.
            result = result.OrderByDescending(route => route.Metrics.PotentialGasPipelineCrossings > 0).ToList();
        }

        return result.AsReadOnly();
    }

    private static bool Similar(
        IReadOnlyList<GeoCoordinate> existing,
        IReadOnlyList<Coordinate> candidate,
        LocalProjection projection,
        double cellSize)
    {
        // A sub-cell deviation is usually not a meaningful alternative corridor. Compare metres,
        // not exact floating-point equality of vertices. 90% overlap suppresses near-duplicates.
        var reference = new LineString(existing.Select(projection.Forward).ToArray());
        var proposed = new LineString(candidate.ToArray());
        Geometry corridor = reference.Buffer(cellSize * 0.75);
        if (proposed.Length == 0)
        {
            return true;
        }

        return proposed.Intersection(corridor).Length / proposed.Length
            >= AlternativeRoutePolicy.MaximumCorridorOverlap;
    }

}
