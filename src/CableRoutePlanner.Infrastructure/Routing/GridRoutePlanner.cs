using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;

namespace CableRoutePlanner.Infrastructure.Routing;

public sealed class GridRoutePlanner(IGeodataRepository data, ILogger<GridRoutePlanner> logger) : IRoutePlanner
{
    private static readonly Action<ILogger, int, long, Exception?> FeaturesLoadedLog =
        LoggerMessage.Define<int, long>(
            LogLevel.Information,
            new EventId(1, nameof(FeaturesLoadedLog)),
            "Loaded {FeatureCount} routing features in {ElapsedMilliseconds} ms");

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
        var queryTimer = Stopwatch.StartNew();
        var features = await data.QueryAsync(bounds, cancellationToken);
        FeaturesLoadedLog(logger, features.Count, queryTimer.ElapsedMilliseconds, null);

        var calculationTimer = Stopwatch.StartNew();
        var routes = await Task.Run<IReadOnlyList<RouteAlternative>>(
            () => Calculate(points, bounds, features, options, cancellationToken),
            cancellationToken);
        RoutesCompletedLog(logger, routes.Count, points.Count, calculationTimer.ElapsedMilliseconds, null);
        return routes;
    }

    private ReadOnlyCollection<RouteAlternative> Calculate(
        IReadOnlyList<GeoCoordinate> points,
        GeoBounds bounds,
        IReadOnlyList<MapFeature> features,
        RoutingOptions options,
        CancellationToken ct)
    {
        var projection = new LocalProjection(
            new((bounds.South + bounds.North) / 2, (bounds.West + bounds.East) / 2));
        var min = projection.Forward(new GeoCoordinate(bounds.South, bounds.West));
        var max = projection.Forward(new GeoCoordinate(bounds.North, bounds.East));
        if (max.X - min.X > 20000 || max.Y - min.Y > 20000)
        {
            throw new PlanningException(
                "Расчётная область превышает 20 км. Разбейте трассу на отдельные проекты.");
        }

        var columns = (int)Math.Floor((max.X - min.X) / options.CellSizeMetres) + 1;
        var rows = (int)Math.Floor((max.Y - min.Y) / options.CellSizeMetres) + 1;
        if ((long)columns * rows > options.MaxNodes)
        {
            throw new PlanningException(
                "Слишком большая область расчёта. Увеличьте размер ячейки или уменьшите область.");
        }

        var nodes = new Coordinate[columns * rows];
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < columns; x++)
            {
                nodes[y * columns + x] = new(
                    min.X + x * options.CellSizeMetres,
                    min.Y + y * options.CellSizeMetres);
            }
        }

        var evaluator = new TerrainEvaluator(features, projection, options.CellSizeMetres);
        var cache = new Dictionary<(int, int), EdgeEvaluation?>();
        var gridEdgeEvaluations = 0;
        EdgeEvaluation? GridEdge(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (!cache.TryGetValue(key, out var edge))
            {
                gridEdgeEvaluations++;
                cache[key] = edge = evaluator.Evaluate(nodes[a], nodes[b]);
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

                var part = Search(
                    start,
                    end,
                    nodes,
                    columns,
                    rows,
                    min,
                    options.CellSizeMetres,
                    profile,
                    evaluator,
                    GridEdge,
                    projection,
                    ct);
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

        SearchCompletedLog(logger, result.Count, nodes.Length, gridEdgeEvaluations, null);
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

    private static List<Coordinate> Search(
        Coordinate start,
        Coordinate end,
        Coordinate[] grid,
        int columns,
        int rows,
        Coordinate min,
        double cell,
        CostProfile profile,
        TerrainEvaluator evaluator,
        Func<int, int, EdgeEvaluation?> gridEdge,
        LocalProjection projection,
        CancellationToken ct)
    {
        var startId = grid.Length;
        var endId = startId + 1;
        Coordinate Node(int id) => id == startId ? start : id == endId ? end : grid[id];
        Dictionary<int, EdgeEvaluation> Connect(Coordinate p)
        {
            var cx = (int)Math.Round((p.X - min.X) / cell);
            var cy = (int)Math.Round((p.Y - min.Y) / cell);
            var connections = new Dictionary<int, EdgeEvaluation>();
            for (var y = Math.Max(0, cy - 1); y <= Math.Min(rows - 1, cy + 1); y++)
            {
                for (var x = Math.Max(0, cx - 1); x <= Math.Min(columns - 1, cx + 1); x++)
                {
                    ct.ThrowIfCancellationRequested();
                    var id = y * columns + x;
                    var edge = evaluator.Evaluate(p, grid[id]);
                    if (edge is not null)
                    {
                        connections[id] = edge;
                    }
                }
            }

            return connections;
        }

        var fromStart = Connect(start);
        var toEnd = Connect(end);
        var direct = start.Distance(end) <= cell * 2 ? evaluator.Evaluate(start, end) : null;
        if (fromStart.Count == 0 || toEnd.Count == 0)
        {
            throw new PlanningException(
                "Точка находится в препятствии или не соединяется с допустимой областью сетки.");
        }

        IEnumerable<(int Id, EdgeEvaluation Edge)> Neighbours(int id)
        {
            if (id == startId)
            {
                foreach (var e in fromStart)
                {
                    yield return (e.Key, e.Value);
                }

                if (direct is not null)
                {
                    yield return (endId, direct);
                }

                yield break;
            }

            if (toEnd.TryGetValue(id, out var finish))
            {
                yield return (endId, finish);
            }

            var x = id % columns;
            var y = id / columns;
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0 || x + dx < 0 || x + dx >= columns || y + dy < 0 || y + dy >= rows)
                    {
                        continue;
                    }

                    var next = (y + dy) * columns + x + dx;
                    // Test the full segment to prevent skipped obstacles and corner cutting.
                    var edge = gridEdge(id, next);
                    if (edge is not null)
                    {
                        yield return (next, edge);
                    }
                }
            }
        }

        var queue = new PriorityQueue<(int Id, double Cost), double>();
        var distances = Enumerable.Repeat(double.PositiveInfinity, grid.Length + 2).ToArray();
        var parents = Enumerable.Repeat(-1, distances.Length).ToArray();
        distances[startId] = 0;
        queue.Enqueue((startId, 0), 0);
        while (queue.TryDequeue(out var current, out _))
        {
            ct.ThrowIfCancellationRequested();
            if (current.Cost > distances[current.Id])
            {
                continue;
            }

            if (current.Id == endId)
            {
                var path = new List<Coordinate>();
                for (var id = endId; id != -1; id = parents[id])
                {
                    path.Add(Node(id));
                }

                path.Reverse();
                return path;
            }

            foreach (var (next, edge) in Neighbours(current.Id))
            {
                var cost = current.Cost + edge.Cost(profile);
                if (cost >= distances[next])
                {
                    continue;
                }

                distances[next] = cost;
                parents[next] = current.Id;
                // 0.49 is below the smallest profile multiplier, with margin for local projection error.
                // The search uses this local metre projection already. Avoid converting each
                // expanded node back to latitude/longitude for a geodesic estimate.
                var heuristic = Node(next).Distance(end) * 0.49;
                queue.Enqueue((next, cost), cost + heuristic);
            }
        }

        throw new PlanningException(
            "В доступной области нет допустимого маршрута. "
            + "Река требует разрешённого или потенциального перехода. "
            + "Можно увеличить область данных или изменить точки.");
    }
}
