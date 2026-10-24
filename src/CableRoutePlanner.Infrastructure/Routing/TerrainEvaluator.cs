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

    public TerrainEvaluator(
        IReadOnlyList<MapFeature> features,
        LocalProjection projection,
        double cellSizeMetres)
    {
        _projection = projection;
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
        _bridgeCorridorMetres = Math.Max(cellSizeMetres * 0.3, 2);
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
        var aligned = new List<Coordinate>(route.Count);
        if (route.Count == 0)
        {
            return aligned;
        }

        aligned.Add(route[0]);
        for (var i = 1; i < route.Count; i++)
        {
            var previous = route[i - 1];
            var current = route[i];
            var segment = _factory.CreateLineString([previous, current]);
            var guide = _bridgeGuides
                .Where(candidate => candidate.River.Intersects(segment)
                    && candidate.Bridge.Distance(segment) <= _bridgeCorridorMetres)
                .OrderBy(candidate => candidate.Bridge.Distance(segment))
                .FirstOrDefault();
            if (guide is not null)
            {
                // Preserve the complete OSM bridge geometry, including both mapped ends. The
                // water intersection alone only snaps the crossing point and leaves the route
                // free to cut diagonally across the bridge approaches.
                var points = guide.Bridge.Coordinates;
                if (points.Length >= 2)
                {
                    if (previous.Distance(points[^1]) < previous.Distance(points[0]))
                    {
                        Array.Reverse(points);
                    }

                    var candidate = new List<Coordinate>(aligned);
                    candidate.AddRange(points);
                    candidate.Add(current);
                    if (IsValidPath(candidate))
                    {
                        aligned.AddRange(points);
                        aligned.Add(current);
                        continue;
                    }
                }
            }

            aligned.Add(current);
        }

        return aligned;
    }

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
        var forest = 0d;
        var field = 0d;
        var clearing = 0d;
        var roads = new List<long>();
        var crossings = new List<long>();
        var bridgeCandidates = new List<long>();
        foreach (var obstacle in _index.Query(line.EnvelopeInternal))
        {
            var geometry = obstacle.Geometry;
            if (!geometry.Intersects(line))
            {
                continue;
            }

            if (obstacle.Kind is TerrainKind.Marsh or TerrainKind.Lake or TerrainKind.River or TerrainKind.Building)
            {
                return null;
            }

            var intersection = geometry.Intersection(line);
            switch (obstacle.Kind)
            {
                case TerrainKind.Forest:
                    forest += intersection.Length;
                    break;
                case TerrainKind.Field:
                    field += intersection.Length;
                    break;
                case TerrainKind.Clearing:
                    clearing += intersection.Length;
                    break;
                case TerrainKind.Road:
                    if (intersection.Dimension == Dimension.Point)
                    {
                        roads.Add(obstacle.Id);
                    }

                    break;
                case TerrainKind.ApprovedCrossing:
                    crossings.Add(obstacle.Id);
                    break;
                case TerrainKind.BridgeCandidate:
                    bridgeCandidates.Add(obstacle.Id);
                    break;
            }
        }

        var projectedLength = line.Length;
        var length = _projection.Inverse(a).DistanceTo(_projection.Inverse(b));
        var scale = projectedLength > 0 ? length / projectedLength : 1;
        return new(
            length,
            Math.Min(forest, projectedLength) * scale,
            Math.Min(field, projectedLength) * scale,
            Math.Min(clearing, projectedLength) * scale,
            roads,
            crossings,
            bridgeCandidates);
    }
}
