using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.IO;
using NetTopologySuite.Geometries.Prepared;

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

    private sealed record Obstacle(
        long Id,
        TerrainKind Kind,
        Geometry Geometry,
        IPreparedGeometry PreparedGeometry);

    private sealed record BridgeGuide(Geometry Bridge, Geometry Water, Geometry WaterSection);

    private readonly record struct ProjectedFeature(MapFeature Feature, Geometry Geometry);

    private readonly record struct BridgeWaterMatch(ProjectedFeature Bridge, Geometry[] WaterObstacles);

    private readonly record struct BridgeSplice(
        List<Coordinate> Path,
        double Length,
        double Cost,
        int BridgeEndIndex);

    private readonly STRtree<Obstacle> _index = new();

    private readonly STRtree<Geometry> _waterOpenings = new();

    private readonly LocalProjection _projection;

    private readonly BridgeGuide[] _bridgeGuides;

    private readonly GridPortal[] _bridgePortals;

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
        var waterIndex = new STRtree<Geometry>();
        Geometry[] waterObstacles = projected
            .Where(f => f.Feature.Kind is TerrainKind.River or TerrainKind.Lake)
            .Select(f => f.Geometry)
            .ToArray();
        foreach (Geometry water in waterObstacles)
        {
            waterIndex.Insert(water.EnvelopeInternal, water);
        }

        waterIndex.Build();
        _bridgeCorridorMetres = Math.Max(cellSizeMetres * 0.45, 2);
        BridgeWaterMatch[] bridgeFeatures = projected
            .Where(f => f.Feature.Kind == TerrainKind.BridgeCandidate)
            .Select(f => new BridgeWaterMatch(
                f,
                waterIndex.Query(f.Geometry.EnvelopeInternal)
                    .Where(water => water.Intersects(f.Geometry))
                    .ToArray()))
            .Where(x => x.WaterObstacles.Length > 0)
            .ToArray();
        _bridgeGuides = bridgeFeatures
            .SelectMany(x => x.WaterObstacles.Select(water =>
            {
                Geometry section = x.Bridge.Geometry.Intersection(water);
                return new BridgeGuide(x.Bridge.Geometry, water, section);
            }))
            .Where(x => !x.WaterSection.IsEmpty && x.WaterSection.Length > 0)
            .ToArray();
        _bridgePortals = bridgeFeatures
            .Select(feature => new GridPortal(feature.Bridge.Geometry.Coordinates))
            .Where(portal => portal.Coordinates.Length >= 2)
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
            if (feature.Kind is TerrainKind.River or TerrainKind.Lake)
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

                _index.Insert(corridor.EnvelopeInternal, new(
                    feature.Id,
                    feature.Kind,
                    corridor,
                    PreparedGeometryFactory.Prepare(corridor)));
                continue;
            }

            if (!geometry.IsEmpty)
            {
                _index.Insert(geometry.EnvelopeInternal, new(
                    feature.Id,
                    feature.Kind,
                    geometry,
                    PreparedGeometryFactory.Prepare(geometry)));
            }
        }

        _index.Build();
    }

    public IReadOnlyList<GridPortal> BridgePortals => _bridgePortals;

    public List<Coordinate> AlignBridgeCrossings(IReadOnlyList<Coordinate> route, CostProfile profile)
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
                .Where(candidate => candidate.Water.Intersects(segment)
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
                            BridgeSplice? splice = TryCreateBridgeSplice(
                                candidate,
                                profile,
                                left + points.Length - 1);
                            if (splice is { } validSplice)
                            {
                                splices.Add(validSplice);
                            }
                        }
                    }

                    if (splices.Count > 0)
                    {
                        BridgeSplice splice = splices
                            .MinBy(candidate => (candidate.Cost, candidate.Length));
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
                candidate.Water.Intersects(segment)
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

    private BridgeSplice? TryCreateBridgeSplice(
        List<Coordinate> path,
        CostProfile profile,
        int bridgeEndIndex)
    {
        double length = 0;
        double cost = 0;
        for (int i = 1; i < path.Count; i++)
        {
            EdgeEvaluation? edge = Evaluate(path[i - 1], path[i]);
            if (edge is null)
            {
                return null;
            }

            length += edge.Length;
            cost += edge.Cost(profile);
        }

        return new BridgeSplice(path, length, cost, bridgeEndIndex);
    }

    public EdgeEvaluation? Evaluate(Coordinate a, Coordinate b)
        => Evaluate(a, b, useProjectedLength: false);

    public EdgeEvaluation? EvaluateForSearch(Coordinate a, Coordinate b)
        => Evaluate(a, b, useProjectedLength: true);

    private EdgeEvaluation? Evaluate(Coordinate a, Coordinate b, bool useProjectedLength)
    {
        LineString line = _factory.CreateLineString([a, b]);
        var contribution = new TerrainContribution();
        foreach (Obstacle obstacle in _index.Query(line.EnvelopeInternal))
        {
            Geometry geometry = obstacle.Geometry;
            if (!obstacle.PreparedGeometry.Intersects(line))
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

            Geometry intersection = obstacle.PreparedGeometry.Covers(line)
                ? line
                : geometry.Intersection(line);
            rule.Apply(intersection, obstacle.Id, contribution);
        }

        double projectedLength = line.Length;
        double length = useProjectedLength
            ? projectedLength
            : _projection.Inverse(a).DistanceTo(_projection.Inverse(b));
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
