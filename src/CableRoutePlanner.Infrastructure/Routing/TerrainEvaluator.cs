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
    public double Cost(CostProfile p) => Length
        - Clearing * (1 - p.Clearing)
        + Forest * p.Forest
        + Field * p.Field
        + (Roads.Count + BridgeCandidates.Count) * p.RoadCrossingMetres;
}

internal sealed class TerrainEvaluator
{
    private sealed record Obstacle(long Id, TerrainKind Kind, Geometry Geometry);

    private sealed record BridgeGuide(Geometry Bridge, Geometry River, Geometry WaterSection);

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
        var projected = features.Select(
            f => (Feature: f, Geometry: projection.Forward(reader.Read(f.Wkb)))).ToArray();
        var approvals = projected.Where(
            f => f.Feature.Kind == TerrainKind.ApprovedCrossing
                && !string.IsNullOrWhiteSpace(f.Feature.ApprovalId))
            .Select(f => f.Geometry)
            .ToArray();
        var riverIndex = new STRtree<Geometry>();
        var rivers = projected.Where(f => f.Feature.Kind == TerrainKind.River).ToArray();
        foreach (var river in rivers)
        {
            riverIndex.Insert(river.Geometry.EnvelopeInternal, river.Geometry);
        }

        riverIndex.Build();
        _bridgeCorridorMetres = Math.Max(cellSizeMetres * 0.45, 2);
        var bridgeFeatures = projected
            .Where(f => f.Feature.Kind == TerrainKind.BridgeCandidate)
            .Select(f => (
                Feature: f,
                Rivers: riverIndex.Query(f.Geometry.EnvelopeInternal)
                    .Where(river => river.Intersects(f.Geometry))
                    .ToArray()))
            .Where(x => x.Rivers.Length > 0)
            .ToArray();
        _bridgeGuides = bridgeFeatures
            .SelectMany(x => x.Rivers.Select(river =>
            {
                var section = x.Feature.Geometry.Intersection(river);
                return new BridgeGuide(x.Feature.Geometry, river, section);
            }))
            .Where(x => !x.WaterSection.IsEmpty && x.WaterSection.Length > 0)
            .ToArray();
        var bridgeOpenings = bridgeFeatures
            .Select(f => (
                Id: f.Feature.Feature.Id,
                // Keep the water opening on the mapped bridge line. A cell-sized buffer let
                // A* cut across the river beside a bridge while still counting that bridge.
                Corridor: f.Feature.Geometry.Buffer(_bridgeCorridorMetres)))
            .ToDictionary(bridge => bridge.Id, bridge => bridge.Corridor);
        foreach (var opening in approvals.Concat(bridgeOpenings.Values))
        {
            _waterOpenings.Insert(opening.EnvelopeInternal, opening);
        }

        _waterOpenings.Build();
        foreach (var (feature, original) in projected)
        {
            var geometry = original;
            if (feature.Kind == TerrainKind.River)
            {
                foreach (var opening in _waterOpenings.Query(geometry.EnvelopeInternal))
                {
                    if (geometry.Intersects(opening))
                    {
                        geometry = geometry.Difference(opening);
                    }
                }
            }

            if (feature.Kind == TerrainKind.BridgeCandidate)
            {
                if (!bridgeOpenings.TryGetValue(feature.Id, out var corridor))
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
        var aligned = route.ToList();
        if (aligned.Count < 2)
        {
            return aligned;
        }

        var usedGuides = new HashSet<BridgeGuide>();
        for (var i = 1; i < aligned.Count; i++)
        {
            var previous = aligned[i - 1];
            var current = aligned[i];
            var segment = _factory.CreateLineString([previous, current]);
            var guide = _bridgeGuides
                .Where(candidate => candidate.River.Intersects(segment)
                    && candidate.Bridge.Distance(segment) <= _bridgeCorridorMetres)
                .OrderBy(candidate => candidate.Bridge.Distance(segment))
                .FirstOrDefault();
            if (guide is not null && usedGuides.Add(guide))
            {
                if (guide.Bridge.Distance(_factory.CreatePoint(previous)) < 1
                    && guide.Bridge.Distance(_factory.CreatePoint(current)) < 1)
                {
                    continue;
                }

                // Replace nearby grid vertices with the actual bridge line. Appending bridge
                // points while retaining those vertices makes a visible kink or short backtrack.
                var points = guide.Bridge.Coordinates;
                if (points.Length >= 2)
                {
                    if (previous.Distance(points[^1]) + current.Distance(points[0])
                        < previous.Distance(points[0]) + current.Distance(points[^1]))
                    {
                        Array.Reverse(points);
                    }

                    var splices = new List<(List<Coordinate> Path, double Length, int BridgeEndIndex)>();
                    var leftAnchors = Enumerable.Range(Math.Max(0, i - 3), i - Math.Max(0, i - 3))
                        .Prepend(0)
                        .Distinct();
                    var rightAnchors = Enumerable.Range(0, Math.Min(3, aligned.Count - i))
                        .Select(offset => i + offset)
                        .Append(aligned.Count - 1)
                        .Distinct();
                    foreach (var left in leftAnchors)
                    {
                        foreach (var right in rightAnchors)
                        {
                            var candidate = aligned.Take(left + 1).ToList();
                            candidate.AddRange(points);
                            candidate.AddRange(aligned.Skip(right));
                            if (IsValidPath(candidate))
                            {
                                splices.Add((candidate, PathLength(candidate), left + points.Length - 1));
                            }
                        }
                    }

                    if (splices.Count > 0)
                    {
                        var splice = splices.MinBy(candidate => candidate.Length);
                        aligned = splice.Path;
                        i = splice.BridgeEndIndex;
                    }
                }
            }
        }

        return aligned;
    }

    private static double PathLength(IReadOnlyList<Coordinate> path) => path
        .Zip(path.Skip(1), (a, b) => a.Distance(b))
        .Sum();

    private bool IsValidPath(IReadOnlyList<Coordinate> path)
    {
        for (var i = 1; i < path.Count; i++)
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
        var line = _factory.CreateLineString([a, b]);
        var contribution = new TerrainContribution();
        foreach (var obstacle in _index.Query(line.EnvelopeInternal))
        {
            var geometry = obstacle.Geometry;
            if (!geometry.Intersects(line))
            {
                continue;
            }

            if (!_rules.TryGet(obstacle.Kind, out var rule))
            {
                continue;
            }

            if (rule.Blocks)
            {
                return null;
            }

            var intersection = geometry.Intersection(line);
            rule.Apply(intersection, obstacle.Id, contribution);
        }

        var projectedLength = line.Length;
        var length = _projection.Inverse(a).DistanceTo(_projection.Inverse(b));
        var scale = projectedLength > 0 ? length / projectedLength : 1;
        return new(
            length,
            Math.Min(contribution.Forest, projectedLength) * scale,
            Math.Min(contribution.Field, projectedLength) * scale,
            Math.Min(contribution.Clearing, projectedLength) * scale,
            contribution.Roads,
            contribution.Crossings,
            contribution.BridgeCandidates);
    }
}
