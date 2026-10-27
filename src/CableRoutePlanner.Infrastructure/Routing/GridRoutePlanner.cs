using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;

namespace CableRoutePlanner.Infrastructure.Routing;

public sealed class GridRoutePlanner(
    IGeodataRepository data,
    IGridGenerator gridGenerator,
    IGridPathfinder pathfinder,
    ILogger<GridRoutePlanner> logger) : IRoutePlanner
{
    private readonly record struct SearchMargins(double LatitudeDegrees, double LongitudeDegrees);

    private const double MinimumSearchMarginMetres = 1_000;
    private const double MaximumSearchMarginMetres = 5_000;

    private readonly PreparedRoutingAreaCache _areas = new(data);

    private static readonly Action<ILogger, long, long, Exception?> PreparationLog =
        LoggerMessage.Define<long, long>(
            LogLevel.Information,
            new EventId(1, nameof(PreparationLog)),
            "Routing SQLite query {QueryMilliseconds} ms; WKB, projection and spatial index {PreparationMilliseconds} ms (zero on cache reuse)");

    private static readonly Action<ILogger, int, int, long, Exception?> RoutesCompletedLog =
        LoggerMessage.Define<int, int, long>(
            LogLevel.Information,
            new EventId(2, nameof(RoutesCompletedLog)),
            "Completed {RouteCount} routes for {WaypointCount} waypoints in {ElapsedMilliseconds} ms");

    private static readonly Action<ILogger, int, int, int, Exception?> SearchCompletedLog =
        LoggerMessage.Define<int, int, int>(
            LogLevel.Information,
            new EventId(3, nameof(SearchCompletedLog)),
            "Calculated {RouteCount} distinct routes over {NodeCount} grid nodes and evaluated {EdgeEvaluations} grid edges");

    public async Task<IReadOnlyList<RouteAlternative>> PlanAsync(
        IReadOnlyList<GeoCoordinate> points,
        RoutingOptions options,
        CancellationToken cancellationToken)
    {
        options.Validate();
        if (points.Count < 2)
        {
            throw new PlanningException("Установите минимум две точки.");
        }

        if (points.Any(p => !data.Info.Bounds.Contains(p)))
        {
            throw new PlanningException(
                "Для выбранной точки нет данных в офлайн-пакете.");
        }

        SearchMargins margins = SearchMarginDegrees(points);
        GeoBounds requested = new(
            points.Min(point => point.Longitude) - margins.LongitudeDegrees,
            points.Min(point => point.Latitude) - margins.LatitudeDegrees,
            points.Max(point => point.Longitude) + margins.LongitudeDegrees,
            points.Max(point => point.Latitude) + margins.LatitudeDegrees);
        GeoBounds coverage = data.Info.Bounds;
        var bounds = new GeoBounds(
            Math.Max(requested.West, coverage.West),
            Math.Max(requested.South, coverage.South),
            Math.Min(requested.East, coverage.East),
            Math.Min(requested.North, coverage.North));
        PreparedRoutingArea area = await _areas.GetAsync(bounds, options.CellSizeMetres, cancellationToken);
        PreparationLog(logger, area.QueryMilliseconds, area.PreparationMilliseconds, null);

        Stopwatch calculationTimer = Stopwatch.StartNew();
        IReadOnlyList<RouteAlternative> routes = await Task.Run<IReadOnlyList<RouteAlternative>>(
            () => Calculate(points, bounds, area, options, cancellationToken),
            cancellationToken);
        RoutesCompletedLog(logger, routes.Count, points.Count, calculationTimer.ElapsedMilliseconds, null);
        return routes;
    }

    private static SearchMargins SearchMarginDegrees(IReadOnlyList<GeoCoordinate> points)
    {
        double spanMetres = points
            .Zip(points.Skip(1), (from, to) => from.DistanceTo(to))
            .DefaultIfEmpty()
            .Max();
        double marginMetres = Math.Clamp(
            Math.Max(MinimumSearchMarginMetres, spanMetres * 0.1),
            MinimumSearchMarginMetres,
            MaximumSearchMarginMetres);
        double latitude = points.Average(point => point.Latitude);
        double latitudeDegrees = marginMetres / (GeoCoordinate.MeanEarthRadiusMetres * Math.PI / 180);
        double longitudeScale = Math.Abs(Math.Cos(latitude * Math.PI / 180));
        double longitudeDegrees = longitudeScale > double.Epsilon
            ? Math.Min(180, latitudeDegrees / longitudeScale)
            : 180;
        return new(latitudeDegrees, longitudeDegrees);
    }

    private ReadOnlyCollection<RouteAlternative> Calculate(
        IReadOnlyList<GeoCoordinate> points,
        GeoBounds bounds,
        PreparedRoutingArea area,
        RoutingOptions options,
        CancellationToken ct)
    {
        LocalProjection projection = area.Projection;
        GridMesh mesh = gridGenerator.Generate(bounds, options, projection);

        TerrainEvaluator evaluator = area.Evaluator;
        var cache = new Dictionary<(int, int), EdgeEvaluation?>();
        int gridEdgeEvaluations = 0;
        EdgeEvaluation? GridEdge(int a, int b)
        {
            (int, int) key = a < b ? (a, b) : (b, a);
            if (!cache.TryGetValue(key, out EdgeEvaluation? edge))
            {
                gridEdgeEvaluations++;
                cache[key] = edge = evaluator.Evaluate(mesh.Nodes[a], mesh.Nodes[b]);
            }

            return edge;
        }

        var result = new List<RouteAlternative>();
        foreach (CostProfile profile in CostProfile.Defaults)
        {
            ct.ThrowIfCancellationRequested();
            profile.Validate();
            var route = new List<Coordinate>();
            for (int leg = 0; leg < points.Count - 1; leg++)
            {
                Coordinate start = projection.Forward(points[leg]);
                Coordinate end = projection.Forward(points[leg + 1]);
                if (start.Equals2D(end))
                {
                    if (route.Count == 0)
                    {
                        route.Add(start);
                    }

                    continue;
                }

                EdgeEvaluation? direct = evaluator.Evaluate(start, end);
                // The direct segment is globally optimal only for a pure length
                // profile: Euclidean distance is a lower bound, and every terrain
                // contribution is nonnegative. Other profiles must search because
                // a longer clearing route can cost less, even when the direct leg
                // contains no obstacle.
                bool lengthOnlyProfile = profile.Clearing == 1
                    && profile.Forest == 0
                    && profile.Field == 0
                    && profile.RoadCrossingMetres == 0;
                List<Coordinate> part = lengthOnlyProfile && direct is not null
                    ? new List<Coordinate> { start, end }
                    : pathfinder.FindPath(
                        start,
                        end,
                        mesh,
                        profile,
                        (from, to) => evaluator.Evaluate(from, to)?.Cost(profile),
                        (from, to) => GridEdge(from, to)?.Cost(profile),
                        ct).ToList();
                // Smooth the grid path first, then impose the surveyed bridge alignment.
                // The latter remains part of the engineering geometry and is not collapsed.
                part = evaluator.AlignBridgeCrossings(evaluator.SimplifyPath(part, profile), profile);
                if (route.Count > 0)
                {
                    part.RemoveAt(0);
                }

                route.AddRange(part);
            }

            if (route.Count < 2)
            {
                throw new PlanningException("Все точки совпадают. Установите разные точки.");
            }

            // Alternatives must represent different geometry; profiles alone do not create an alternative.
            if (result.Any(r => Similar(r.Geometry, route, projection, options.CellSizeMetres)))
            {
                continue;
            }

            double length = 0, forest = 0, field = 0, cost = 0;
            var roads = new HashSet<long>();
            var crossings = new HashSet<long>();
            var bridgeCandidates = new HashSet<long>();
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
                        null),
                    Array.AsReadOnly(evaluator.BridgeTransitionPoints(route)
                        .Select(projection.Inverse).ToArray())));
        }

        SearchCompletedLog(logger, result.Count, mesh.Nodes.Length, gridEdgeEvaluations, null);
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
