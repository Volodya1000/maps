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

    private readonly STRtree<Obstacle> _index = new();

    private readonly STRtree<Geometry> _waterOpenings = new();

    private readonly LocalProjection _projection;

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
        foreach (var river in projected.Where(f => f.Feature.Kind == TerrainKind.River))
        {
            riverIndex.Insert(river.Geometry.EnvelopeInternal, river.Geometry);
        }

        riverIndex.Build();
        var bridgeOpenings = projected
            .Where(f => f.Feature.Kind == TerrainKind.BridgeCandidate)
            .Where(f => riverIndex.Query(f.Geometry.EnvelopeInternal)
                .Any(river => river.Intersects(f.Geometry)))
            .Select(f => (
                Id: f.Feature.Id,
                Corridor: f.Geometry.Buffer(Math.Max(cellSizeMetres * 0.75, 4))))
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
