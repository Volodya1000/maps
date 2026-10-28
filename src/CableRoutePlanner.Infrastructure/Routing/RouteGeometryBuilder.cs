using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using NetTopologySuite.Geometries;

namespace CableRoutePlanner.Infrastructure.Routing;

internal sealed class RouteGeometryBuilder(IGridPathfinder pathfinder)
{
    public List<Coordinate> Build(IReadOnlyList<GeoCoordinate> points, LocalProjection projection,
        TerrainEvaluator evaluator, GridMesh mesh, CostProfile profile,
        Func<int, int, EdgeEvaluation?> gridEdge, CancellationToken ct)
    {
        List<Coordinate> route = [];
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
                    (from, to) => evaluator.EvaluateForSearch(from, to)?.Cost(profile),
                    (from, to) => gridEdge(from, to)?.Cost(profile),
                    ct).ToList();
            // Smooth the grid path first, then impose the mapped bridge alignment.
            // The latter remains part of the engineering geometry and is not collapsed.
            part = evaluator.AlignBridgeCrossings(evaluator.SimplifyPath(part, profile), profile);
            if (route.Count > 0)
            {
                part.RemoveAt(0);
            }

            route.AddRange(part);
        }

        return route;
    }
}
