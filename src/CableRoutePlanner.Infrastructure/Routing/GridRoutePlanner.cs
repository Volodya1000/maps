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
    public GridRoutePlanner(IGeodataRepository data, ILogger<GridRoutePlanner> logger)
        : this(data, new GridGenerator(), new AStarGridPathfinder(), logger)
    {
    }

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

        if (points.Count > 100)
        {
            throw new PlanningException("Слишком много точек (максимум 100).");
        }

        if (points.Any(p => !data.Info.Bounds.Contains(p)))
        {
            throw new PlanningException(
                "Для выбранной точки нет данных в офлайн-пакете.");
        }

        var requested = GeoBounds.Around(points);
        var coverage = data.Info.Bounds;
        var bounds = new GeoBounds(
            Math.Max(requested.West, coverage.West),
            Math.Max(requested.South, coverage.South),
            Math.Min(requested.East, coverage.East),
            Math.Min(requested.North, coverage.North));
        var area = await _areas.GetAsync(bounds, options.CellSizeMetres, cancellationToken);
        PreparationLog(logger, area.QueryMilliseconds, area.PreparationMilliseconds, null);

        var calculationTimer = Stopwatch.StartNew();
        var routes = await Task.Run<IReadOnlyList<RouteAlternative>>(
            () => Calculate(points, bounds, area, options, cancellationToken),
            cancellationToken);
        RoutesCompletedLog(logger, routes.Count, points.Count, calculationTimer.ElapsedMilliseconds, null);
        return routes;
    }

    private ReadOnlyCollection<RouteAlternative> Calculate(
        IReadOnlyList<GeoCoordinate> points,
        GeoBounds bounds,
        PreparedRoutingArea area,
        RoutingOptions options,
        CancellationToken ct)
    {
        var projection = area.Projection;
        var mesh = gridGenerator.Generate(bounds, options, projection);

        var evaluator = area.Evaluator;
        var cache = new Dictionary<(int, int), EdgeEvaluation?>();
        var gridEdgeEvaluations = 0;
        EdgeEvaluation? GridEdge(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (!cache.TryGetValue(key, out var edge))
            {
                gridEdgeEvaluations++;
                cache[key] = edge = evaluator.Evaluate(mesh.Nodes[a], mesh.Nodes[b]);
            }

            return edge;
        }

        var result = new List<RouteAlternative>();
        foreach (var profile in CostProfile.Defaults)
        {
            ct.ThrowIfCancellationRequested();
            profile.Validate();
            var route = new List<Coordinate>();
            for (var leg = 0; leg < points.Count - 1; leg++)
            {
                var start = projection.Forward(points[leg]);
                var end = projection.Forward(points[leg + 1]);
                if (start.Equals2D(end))
                {
                    if (route.Count == 0)
                    {
                        route.Add(start);
                    }

                    continue;
                }

                var part = pathfinder.FindPath(
                    start,
                    end,
                    mesh,
                    profile,
                    (from, to) => evaluator.Evaluate(from, to)?.Cost(profile),
                    (from, to) => GridEdge(from, to)?.Cost(profile),
                    ct);
                part = evaluator.AlignBridgeCrossings(part);
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
            for (var i = 1; i < route.Count; i++)
            {
                var edge = evaluator.Evaluate(route[i - 1], route[i])
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
                        null)));
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
        const double maximumCorridorOverlap = 0.90;
        var reference = new LineString(existing.Select(projection.Forward).ToArray());
        var proposed = new LineString(candidate.ToArray());
        var corridor = reference.Buffer(cellSize * 0.75);
        if (proposed.Length == 0)
        {
            return true;
        }

        return proposed.Intersection(corridor).Length / proposed.Length >= maximumCorridorOverlap;
    }

}
