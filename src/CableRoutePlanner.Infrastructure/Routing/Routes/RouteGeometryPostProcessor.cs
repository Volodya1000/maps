using CableRoutePlanner.Domain;
using NetTopologySuite.Geometries;

namespace CableRoutePlanner.Infrastructure.Routing;

/// <summary>Refines complete route geometry while preserving segment permission and profile cost constraints.</summary>
/// <remarks>
/// Simplification precedes mapped-bridge alignment. Candidate splices must be simple
/// and every resulting segment is re-evaluated. All traversal decisions still come
/// from the same evaluator; this class creates no new permissions.
/// </remarks>
internal sealed class RouteGeometryPostProcessor(TerrainEvaluator evaluator, PreparedTerrainData terrain)
{
    private const double CostComparisonTolerance = 0.001;
    private const int MaximumSimplificationLookahead = 64;
    private const double BridgeAlignmentSearchDistanceMetres = 15;
    private const double AlreadyAlignedDistanceMetres = 1;
    private const int NearbyAnchorCount = 3;
    private readonly GeometryFactory _factory = new();
    private readonly record struct BridgeSplice(List<Coordinate> Path, double Length, double Cost, int BridgeEndIndex);

    /// <summary>Replaces nearby grid detours with mapped bridge geometry using the least-cost valid splice.</summary>
    /// <param name="route">A segment-valid path; the input list is not mutated.</param>
    /// <param name="profile">The same engineering weights used to find this alternative.</param>
    /// <returns>A new path with validated, simple bridge splices, ordered by cost then physical length.</returns>
    public List<Coordinate> AlignBridgeCrossings(IReadOnlyList<Coordinate> route, CostProfile profile)
    {
        List<Coordinate> aligned = route.ToList();
        if (aligned.Count < 2)
        {
            return aligned;
        }

        var usedGuides = new HashSet<BridgeGuide>();
        int nextSegment = 1;
        while (nextSegment < aligned.Count)
        {
            int segmentIndex = nextSegment++;
            Coordinate previous = aligned[segmentIndex - 1];
            Coordinate current = aligned[segmentIndex];
            LineString segment = _factory.CreateLineString([previous, current]);
            BridgeGuide? guide = terrain.BridgeGuides
                .Where(candidate => candidate.Water.Intersects(segment)
                    && candidate.Bridge.Distance(segment) <= BridgeAlignmentSearchDistanceMetres)
                .OrderBy(candidate => candidate.Bridge.Distance(segment))
                .FirstOrDefault();
            if (guide is null || !usedGuides.Add(guide))
            {
                continue;
            }

            if (guide.Bridge.Distance(_factory.CreatePoint(previous)) < AlreadyAlignedDistanceMetres
                && guide.Bridge.Distance(_factory.CreatePoint(current)) < AlreadyAlignedDistanceMetres)
            {
                continue;
            }

            // A bridge is considered once, including when its nearest occurrence
            // needs no change. Selecting a different guide here would alter ties.
            BridgeSplice? replacement = FindBridgeSplice(aligned, segmentIndex, guide, profile);
            if (replacement is { } splice)
            {
                aligned = splice.Path;
                nextSegment = splice.BridgeEndIndex + 1;
            }
        }

        return aligned;
    }

    /// <summary>Finds distinct water-entry/exit coordinates for bridges actually approached by the route.</summary>
    /// <remarks>Markers describe mapped candidates and do not certify engineering approval.</remarks>
    public IReadOnlyList<Coordinate> BridgeTransitionPoints(IReadOnlyList<Coordinate> route)
    {
        var points = new Dictionary<(double X, double Y), Coordinate>();
        for (int index = 1; index < route.Count; index++)
        {
            LineString segment = _factory.CreateLineString([route[index - 1], route[index]]);
            foreach (BridgeGuide? guide in terrain.BridgeGuides.Where(candidate =>
                candidate.Water.Intersects(segment)
                && candidate.Bridge.Distance(segment) <= PreparedTerrainData.BridgeCorridorToleranceMetres))
            {
                Coordinate[] waterSection = guide.WaterSection.Coordinates;
                if (waterSection.Length > 0)
                {
                    // The entry and exit are the meaningful display transitions. The
                    // source geometry can contain many technical vertices along one bridge.
                    Coordinate first = waterSection[0];
                    Coordinate last = waterSection[^1];
                    points.TryAdd((first.X, first.Y), first);
                    points.TryAdd((last.X, last.Y), last);
                }
            }
        }

        return points.Values.ToArray();
    }

    /// <summary>Greedily chooses the farthest permitted shortcut that does not increase the profile cost.</summary>
    /// <remarks>
    /// The 64-vertex lookahead bounds geometric work. Costs are compared with a 0.001
    /// cost-unit rounding tolerance. Endpoints are retained; every shortcut uses the
    /// same full-segment evaluator as search. This is not a globally optimal smoother.
    /// </remarks>
    public List<Coordinate> SimplifyPath(IReadOnlyList<Coordinate> path, CostProfile profile)
    {
        if (path.Count < 3)
        {
            return path.ToList();
        }

        var simplified = new List<Coordinate> { path[0] };
        int anchor = 0;
        while (anchor < path.Count - 1)
        {
            int best = anchor + 1;
            double originalCost = 0d;
            int lastCandidate = Math.Min(path.Count - 1, anchor + MaximumSimplificationLookahead);
            for (int candidate = anchor + 1; candidate <= lastCandidate; candidate++)
            {
                EdgeEvaluation? originalEdge = evaluator.Evaluate(path[candidate - 1], path[candidate]);
                if (originalEdge is null)
                {
                    break;
                }

                originalCost += originalEdge.Cost(profile);
                EdgeEvaluation? direct = evaluator.Evaluate(path[anchor], path[candidate]);
                if (direct is not null && direct.Cost(profile) <= originalCost + CostComparisonTolerance)
                {
                    best = candidate;
                }
            }

            simplified.Add(path[best]);
            anchor = best;
        }

        return simplified;
    }

    /// <summary>Tries nearby anchors and both route endpoints without retaining folded grid detours.</summary>
    private BridgeSplice? FindBridgeSplice(
        IReadOnlyList<Coordinate> aligned, int segmentIndex, BridgeGuide guide, CostProfile profile)
    {
        Coordinate[] points = guide.Bridge.Coordinates;
        if (points.Length < 2)
        {
            return null;
        }

        Coordinate previous = aligned[segmentIndex - 1];
        Coordinate current = aligned[segmentIndex];
        if (previous.Distance(points[^1]) + current.Distance(points[0])
            < previous.Distance(points[0]) + current.Distance(points[^1]))
        {
            Array.Reverse(points);
        }

        int firstLeftAnchor = Math.Max(0, segmentIndex - NearbyAnchorCount);
        IEnumerable<int> leftAnchors = Enumerable.Range(firstLeftAnchor, segmentIndex - firstLeftAnchor)
            .Prepend(0).Distinct();
        IEnumerable<int> rightAnchors = Enumerable.Range(0, Math.Min(NearbyAnchorCount, aligned.Count - segmentIndex))
            .Select(offset => segmentIndex + offset).Append(aligned.Count - 1).Distinct();
        List<BridgeSplice> splices = [];
        foreach (int left in leftAnchors)
        {
            foreach (int right in rightAnchors)
            {
                List<Coordinate> candidate = aligned.Take(left + 1).ToList();
                candidate.AddRange(points);
                candidate.AddRange(aligned.Skip(right));
                BridgeSplice? splice = TryCreateBridgeSplice(candidate, profile, left + points.Length - 1);
                if (splice is { } validSplice)
                {
                    splices.Add(validSplice);
                }
            }
        }

        return splices.Count == 0 ? null : splices.MinBy(candidate => (candidate.Cost, candidate.Length));
    }

    private BridgeSplice? TryCreateBridgeSplice(
        List<Coordinate> path,
        CostProfile profile,
        int bridgeEndIndex)
    {
        // A valid segment-by-segment detour can still form a loop or a folded
        // bridge spur. Reject the entire splice before considering its cost.
        if (!_factory.CreateLineString(path.ToArray()).IsSimple)
        {
            return null;
        }

        double length = 0;
        double cost = 0;
        for (int i = 1; i < path.Count; i++)
        {
            EdgeEvaluation? edge = evaluator.Evaluate(path[i - 1], path[i]);
            if (edge is null)
            {
                return null;
            }

            length += edge.Length;
            cost += edge.Cost(profile);
        }

        return new BridgeSplice(path, length, cost, bridgeEndIndex);
    }

}
