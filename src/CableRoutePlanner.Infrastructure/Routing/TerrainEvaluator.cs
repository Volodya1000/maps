using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.IO;

namespace CableRoutePlanner.Infrastructure.Routing;

internal sealed record EdgeEvaluation(
    double Length,
    double Forest,
    double Field,
    double Clearing,
    IReadOnlyList<long> Roads,
    IReadOnlyList<long> Crossings,
    IReadOnlyList<long> BridgeCandidates)
{
    public double Cost(CostProfile profile) => RouteCostCalculator.Calculate(
        new SegmentTerrainFacts(
            Length,
            Forest,
            Field,
            Clearing,
            Roads.Count,
            BridgeCandidates.Count,
            Crossings.Count),
        profile);
}

internal sealed class TerrainEvaluator
{
    private const double CostComparisonTolerance = 0.001;

    private const int MaximumSimplificationLookahead = 64;

    private sealed record Obstacle(long Id, TerrainKind Kind, Geometry Geometry);

    private sealed record BridgeGuide(Geometry Bridge, Geometry River, Geometry WaterSection);

    private readonly record struct ProjectedFeature(MapFeature Feature, Geometry Geometry);

    private readonly record struct BridgeRiverMatch(ProjectedFeature Bridge, Geometry[] Rivers);

    private readonly record struct BridgeSplice(List<Coordinate> Path, double Length, int BridgeEndIndex);

    private readonly STRtree<Obstacle> _index = new();

    private readonly STRtree<Geometry> _waterOpenings = new();

    private readonly LocalProjection _projection;

    private readonly BridgeGuide[] _bridgeGuides;

    private readonly double _bridgeCorridorMetres;

    private readonly GeometryFactory _factory = new();

    private readonly TerrainRules _rules;

    public TerrainEvaluator(
        IReadOnlyList<MapFeature> features,
        LocalProjection projection,
        double cellSizeMetres,
        TerrainRules? rules = null)
    {
        _projection = projection;
        _rules = rules ?? new TerrainRules();
        var reader = new WKBReader();
        ProjectedFeature[] projected = features.Select(
            f => new ProjectedFeature(f, projection.Forward(reader.Read(f.Wkb)))).ToArray();
        Geometry[] approvals = projected.Where(
            f => f.Feature.Kind == TerrainKind.ApprovedCrossing
                && !string.IsNullOrWhiteSpace(f.Feature.ApprovalId))
            .Select(f => f.Geometry)
            .ToArray();
        var riverIndex = new STRtree<Geometry>();
        Geometry[] rivers = projected.Where(f => f.Feature.Kind == TerrainKind.River)
            .Select(f => f.Geometry)
            .ToArray();
        foreach (Geometry river in rivers)
        {
            riverIndex.Insert(river.EnvelopeInternal, river);
        }

        riverIndex.Build();
        _bridgeCorridorMetres = Math.Max(cellSizeMetres * 0.45, 2);
        BridgeRiverMatch[] bridgeFeatures = projected
            .Where(f => f.Feature.Kind == TerrainKind.BridgeCandidate)
            .Select(f => new BridgeRiverMatch(
                f,
                riverIndex.Query(f.Geometry.EnvelopeInternal)
                    .Where(river => river.Intersects(f.Geometry))
                    .ToArray()))
            .Where(x => x.Rivers.Length > 0)
            .ToArray();
        _bridgeGuides = bridgeFeatures
            .SelectMany(x => x.Rivers.Select(river =>
            {
                Geometry section = x.Bridge.Geometry.Intersection(river);
                return new BridgeGuide(x.Bridge.Geometry, river, section);
            }))
            .Where(x => !x.WaterSection.IsEmpty && x.WaterSection.Length > 0)
            .ToArray();
        Dictionary<long, Geometry> bridgeOpenings = bridgeFeatures
            .Select(f => (
                Id: f.Bridge.Feature.Id,
                // Keep the water opening on the mapped bridge line. A cell-sized buffer let
                // A* cut across the river beside a bridge while still counting that bridge.
                Corridor: f.Bridge.Geometry.Buffer(_bridgeCorridorMetres)))
            .ToDictionary(bridge => bridge.Id, bridge => bridge.Corridor);
        foreach (Geometry? opening in approvals.Concat(bridgeOpenings.Values))
        {
            _waterOpenings.Insert(opening.EnvelopeInternal, opening);
        }

        _waterOpenings.Build();
        foreach (ProjectedFeature projectedFeature in projected)
        {
            MapFeature feature = projectedFeature.Feature;
            Geometry geometry = projectedFeature.Geometry;
            if (feature.Kind == TerrainKind.River)
            {
                foreach (Geometry opening in _waterOpenings.Query(geometry.EnvelopeInternal))
                {
                    if (geometry.Intersects(opening))
                    {
                        geometry = geometry.Difference(opening);
                    }
                }
            }

            if (feature.Kind == TerrainKind.BridgeCandidate)
            {
                if (!bridgeOpenings.TryGetValue(feature.Id, out Geometry? corridor))
                {
                    continue;
                }

                _index.Insert(corridor.EnvelopeInternal, new(feature.Id, feature.Kind, corridor));
                continue;
            }

            if (!geometry.IsEmpty)
            {
                _index.Insert(geometry.EnvelopeInternal, new(feature.Id, feature.Kind, geometry));
            }
        }

        _index.Build();
    }

    public List<Coordinate> AlignBridgeCrossings(IReadOnlyList<Coordinate> route)
    {
        List<Coordinate> aligned = route.ToList();
        if (aligned.Count < 2)
        {
            return aligned;
        }

        var usedGuides = new HashSet<BridgeGuide>();
        int i = 1;
        while (i < aligned.Count)
        {
            Coordinate previous = aligned[i - 1];
            Coordinate current = aligned[i];
            LineString segment = _factory.CreateLineString([previous, current]);
            BridgeGuide? guide = _bridgeGuides
                .Where(candidate => candidate.River.Intersects(segment)
                    && candidate.Bridge.Distance(segment) <= _bridgeCorridorMetres)
                .OrderBy(candidate => candidate.Bridge.Distance(segment))
                .FirstOrDefault();
            if (guide is not null && usedGuides.Add(guide))
            {
                if (guide.Bridge.Distance(_factory.CreatePoint(previous)) < 1
                    && guide.Bridge.Distance(_factory.CreatePoint(current)) < 1)
                {
                    i++;
                    continue;
                }

                // Replace nearby grid vertices with the actual bridge line. Appending bridge
                // points while retaining those vertices makes a visible kink or short backtrack.
                Coordinate[] points = guide.Bridge.Coordinates;
                if (points.Length >= 2)
                {
                    if (previous.Distance(points[^1]) + current.Distance(points[0])
                        < previous.Distance(points[0]) + current.Distance(points[^1]))
                    {
                        Array.Reverse(points);
                    }

                    List<BridgeSplice> splices = new();
                    IEnumerable<int> leftAnchors = Enumerable.Range(Math.Max(0, i - 3), i - Math.Max(0, i - 3))
                        .Prepend(0)
                        .Distinct();
                    IEnumerable<int> rightAnchors = Enumerable.Range(0, Math.Min(3, aligned.Count - i))
                        .Select(offset => i + offset)
                        .Append(aligned.Count - 1)
                        .Distinct();
                    foreach (int left in leftAnchors)
                    {
                        foreach (int right in rightAnchors)
                        {
                            List<Coordinate> candidate = aligned.Take(left + 1).ToList();
                            candidate.AddRange(points);
                            candidate.AddRange(aligned.Skip(right));
                            if (IsValidPath(candidate))
                            {
                                splices.Add(new BridgeSplice(candidate, PathLength(candidate), left + points.Length - 1));
                            }
                        }
                    }

                    if (splices.Count > 0)
                    {
                        BridgeSplice splice = splices.MinBy(candidate => candidate.Length);
                        aligned = splice.Path;
                        i = splice.BridgeEndIndex;
                    }
                }
            }

            i++;
        }

        return aligned;
    }

    public IReadOnlyList<Coordinate> BridgeTransitionPoints(IReadOnlyList<Coordinate> route)
    {
        var points = new Dictionary<(double X, double Y), Coordinate>();
        for (int index = 1; index < route.Count; index++)
        {
            LineString segment = _factory.CreateLineString([route[index - 1], route[index]]);
            foreach (BridgeGuide? guide in _bridgeGuides.Where(candidate =>
                candidate.River.Intersects(segment)
                && candidate.Bridge.Distance(segment) <= _bridgeCorridorMetres))
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
                EdgeEvaluation? originalEdge = Evaluate(path[candidate - 1], path[candidate]);
                if (originalEdge is null)
                {
                    break;
                }

                originalCost += originalEdge.Cost(profile);
                EdgeEvaluation? direct = Evaluate(path[anchor], path[candidate]);
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

    private static double PathLength(IReadOnlyList<Coordinate> path) => path
        .Zip(path.Skip(1), (a, b) => a.Distance(b))
        .Sum();

    private bool IsValidPath(IReadOnlyList<Coordinate> path)
    {
        for (int i = 1; i < path.Count; i++)
        {
            if (Evaluate(path[i - 1], path[i]) is null)
            {
                return false;
            }
        }

        return true;
    }

    public EdgeEvaluation? Evaluate(Coordinate a, Coordinate b)
    {
        LineString line = _factory.CreateLineString([a, b]);
        var contribution = new TerrainContribution();
        foreach (Obstacle obstacle in _index.Query(line.EnvelopeInternal))
        {
            Geometry geometry = obstacle.Geometry;
            if (!geometry.Intersects(line))
            {
                continue;
            }

            if (!_rules.TryGet(obstacle.Kind, out ITerrainRule? rule))
            {
                continue;
            }

            if (rule.Blocks)
            {
                return null;
            }

            Geometry intersection = geometry.Intersection(line);
            rule.Apply(intersection, obstacle.Id, contribution);
        }

        double projectedLength = line.Length;
        double length = _projection.Inverse(a).DistanceTo(_projection.Inverse(b));
        double scale = projectedLength > 0 ? length / projectedLength : 1;
        return new(
            length,
            Math.Min(Math.Min(contribution.Forest, projectedLength) * scale, length),
            Math.Min(Math.Min(contribution.Field, projectedLength) * scale, length),
            Math.Min(Math.Min(contribution.Clearing, projectedLength) * scale, length),
            contribution.Roads,
            contribution.Crossings,
            contribution.BridgeCandidates);
    }
}
