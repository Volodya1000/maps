using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;

namespace CableRoutePlanner.Infrastructure.Routing;

/// <summary>Owns one calculation's grid-edge cache and builds ordered waypoint legs for each profile.</summary>
internal sealed class RouteGeometryBuilder
{
    private readonly IGridPathfinder _pathfinder;
    private readonly GridMesh _mesh;
    private readonly TerrainEvaluator _evaluator;
    private readonly LocalProjection _projection;
    private readonly RouteGeometryPostProcessor _postProcessor;
    private readonly ILogger _logger;
    private readonly Guid _calculationId;

    public GridEdgeEvaluationCache Edges { get; }

    public RouteGeometryBuilder(IGridPathfinder pathfinder, GridMesh mesh, TerrainEvaluator evaluator,
        LocalProjection projection, RouteGeometryPostProcessor postProcessor, ILogger? logger = null,
        Guid calculationId = default)
    {
        _pathfinder = pathfinder;
        _mesh = mesh;
        _evaluator = evaluator;
        _projection = projection;
        _postProcessor = postProcessor;
        _logger = logger ?? NullLogger.Instance;
        _calculationId = calculationId;
        Edges = new(mesh, evaluator);
    }

    /// <summary>Builds, simplifies and bridge-aligns each leg before concatenating the waypoint route.</summary>
    /// <param name="points">Ordered geographic waypoints; repeated adjacent points do not create an edge.</param>
    /// <param name="profile">One validated profile, also used to constrain shortcut costs.</param>
    /// <param name="ct">Cancels graph construction and path search.</param>
    /// <returns>Projected route coordinates with shared leg endpoints included once.</returns>
    /// <exception cref="PlanningException">A leg cannot connect to the mesh or has no permitted graph path.</exception>
    public List<Coordinate> Build(IReadOnlyList<GeoCoordinate> points, CostProfile profile, CancellationToken ct)
    {
        List<Coordinate> route = [];
        for (int leg = 0; leg < points.Count - 1; leg++)
        {
            Coordinate start = _projection.Forward(points[leg]);
            Coordinate end = _projection.Forward(points[leg + 1]);
            if (start.Equals2D(end))
            {
                if (route.Count == 0)
                {
                    route.Add(start);
                }

                continue;
            }

            EdgeEvaluation? direct = _evaluator.EvaluateForSearch(start, end);
            // The direct segment is globally optimal only for a pure length
            // profile: Euclidean distance is a lower bound, and every terrain
            // contribution is nonnegative. Other profiles must search because
            // a longer clearing route can cost less, even when the direct leg
            // contains no obstacle.
            bool lengthOnlyProfile = profile.Clearing == 1
                && profile.Forest == 0
                && profile.Field == 0
                && profile.RoadCrossingMetres == 0;
            List<Coordinate> part;
            if (lengthOnlyProfile && direct is not null)
            {
                part = [start, end];
                RoutingLog.LegSearchLog(_logger, _calculationId, profile.Name, leg, null, false, 0,
                    Edges.EvaluationCount, true);
            }
            else
            {
                GridSearchGraph? graph = null;
                try
                {
                    graph = new(start, end, _mesh, profile, _evaluator, Edges, ct, _logger, _calculationId, leg);
                    IReadOnlyList<int> path = _pathfinder.FindPath(graph, ct) ?? throw new PlanningException(
                        "В доступной области нет допустимого маршрута. "
                        + $"Сетка {_mesh.CellSizeMetres:F0} м может не представить узкий проход; алгоритм не гарантирует полноту. "
                        + "Можно увеличить область данных или изменить точки.",
                        PlanningErrorReason.NoRouteInSearchArea);
                    part = path.Select(graph.GetCoordinate).ToList();
                    RoutingLog.LegSearchLog(_logger, _calculationId, profile.Name, leg, null, false,
                        graph.ExpandedNodes, Edges.EvaluationCount, false);
                }
                catch (PlanningException error)
                {
                    RoutingLog.LegSearchLog(_logger, _calculationId, profile.Name, leg, error.Reason, false,
                        graph?.ExpandedNodes ?? 0, Edges.EvaluationCount, false);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    RoutingLog.LegSearchLog(_logger, _calculationId, profile.Name, leg, null, true,
                        graph?.ExpandedNodes ?? 0, Edges.EvaluationCount, false);
                    throw;
                }
            }
            // Smooth the grid path first, then impose the mapped bridge alignment.
            // The latter remains part of the engineering geometry and is not collapsed.
            part = _postProcessor.AlignBridgeCrossings(_postProcessor.SimplifyPath(part, profile), profile);
            if (route.Count > 0)
            {
                part.RemoveAt(0);
            }

            route.AddRange(part);
        }

        return route;
    }
}
