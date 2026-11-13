using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.IO;

namespace CableRoutePlanner.Infrastructure.Routing;

internal readonly record struct ProjectedMapFeature(MapFeature Feature, Geometry Geometry);
internal sealed record BridgeGuide(Geometry Bridge, Geometry Water, Geometry WaterSection);
internal sealed record TerrainObstacle(long Id, TerrainKind Kind, Geometry Geometry, IPreparedGeometry PreparedGeometry);

/// <summary>Read-only spatial snapshot of one loaded routing area, independent of a search or cost profile.</summary>
/// <remarks>
/// Projects each WKB once, prepares full-segment obstacle tests and mapped transition
/// corridors, and retains only area-bounded indices. Published NTS geometries must not
/// be mutated. Portal and index insertion order intentionally preserve route tie choices.
/// </remarks>
internal sealed class PreparedTerrainData
{
    public const double BridgeCorridorToleranceMetres = 2;
    private readonly record struct BridgeWaterMatch(ProjectedMapFeature Bridge, Geometry[] WaterObstacles);

    public LocalProjection Projection { get; }
    public IPreparedGeometry? Coverage { get; }
    public Envelope? FullyCoveredArea { get; }
    public STRtree<TerrainObstacle> Index { get; } = new();
    public (long Id, Geometry Geometry, Geometry Corridor)[] BridgeCrossingCandidates { get; }
    public BridgeGuide[] BridgeGuides { get; }
    public GasCrossingIndex GasCrossings { get; }
    public GasCrossingIndex UnderpassCrossings { get; }
    public RoadContactAnalyzer RoadContacts { get; }
    public IReadOnlyList<GridPortal> TransitionPortals { get; }

    public PreparedTerrainData(IReadOnlyList<MapFeature> features, LocalProjection projection,
        Geometry? coverage = null, Envelope? fullyCoveredArea = null)
    {
        Coverage = coverage is null ? null : PreparedGeometryFactory.Prepare(coverage);
        FullyCoveredArea = fullyCoveredArea;
        Projection = projection;
        foreach (MapFeature feature in features)
        {
            // Validate even objects outside a tested segment. Unsupported data must
            // not look like a successful route through an implicitly neutral type.
            _ = TerrainTraversalPolicy.For(feature.Kind);
        }
        var reader = new WKBReader();
        ProjectedMapFeature[] input = features.Select(
            f => new ProjectedMapFeature(f, projection.Forward(reader.Read(f.Wkb)))).ToArray();
        ProjectedMapFeature[] projected = input.OrderBy(feature => feature.Feature.Id).ToArray();
        GasCrossings = new GasCrossingIndex(input);
        UnderpassCrossings = new GasCrossingIndex(input, TerrainKind.PedestrianUnderpassCandidate);
        BridgeCrossingCandidates = projected
            .Where(feature => feature.Feature.Kind == TerrainKind.BridgeCandidate)
            .Select(feature => (feature.Feature.Id, feature.Geometry,
                feature.Geometry.Buffer(BridgeCorridorToleranceMetres)))
            .ToArray();
        RoadContacts = new RoadContactAnalyzer(projected
            .Where(feature => feature.Feature.Kind is TerrainKind.Road or TerrainKind.BridgeCandidate)
            .Select(feature => (feature.Feature.Id, feature.Geometry)), projection);
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
        BridgeWaterMatch[] bridgeFeatures = projected
            .Where(f => f.Feature.Kind == TerrainKind.BridgeCandidate)
            .Select(f => new BridgeWaterMatch(
                f,
                waterIndex.Query(f.Geometry.EnvelopeInternal)
                    .Where(water => water.Intersects(f.Geometry))
                    .ToArray()))
            .Where(x => x.WaterObstacles.Length > 0)
            .ToArray();
        BridgeGuides = bridgeFeatures
            .SelectMany(x => x.WaterObstacles.Select(water =>
            {
                Geometry section = x.Bridge.Geometry.Intersection(water);
                return new BridgeGuide(x.Bridge.Geometry, water, section);
            }))
            .Where(x => !x.WaterSection.IsEmpty && x.WaterSection.Length > 0)
            .ToArray();
        GridPortal[] bridgePortals = bridgeFeatures
            .Select(feature => new GridPortal(feature.Bridge.Geometry.Coordinates))
            .Concat(projected
                .Where(feature => feature.Feature.Kind == TerrainKind.BridgeCandidate
                    && feature.Geometry is LineString)
                .Select(feature => new GridPortal(feature.Geometry.Coordinates)))
            .Where(portal => portal.Coordinates.Length >= 2)
            .ToArray();
        Dictionary<long, Geometry> bridgeOpenings = bridgeFeatures
            .Select(f => (
                Id: f.Bridge.Feature.Id,
                // Keep the water opening on the mapped bridge line. A cell-sized buffer let
                // A* cut across the river beside a bridge while still counting that bridge.
                Corridor: f.Bridge.Geometry.Buffer(BridgeCorridorToleranceMetres)))
            .ToDictionary(bridge => bridge.Id, bridge => bridge.Corridor);
        STRtree<Geometry> waterOpenings = new();
        foreach (Geometry? opening in approvals.Concat(bridgeOpenings.Values))
        {
            waterOpenings.Insert(opening.EnvelopeInternal, opening);
        }

        waterOpenings.Build();
        foreach (ProjectedMapFeature projectedFeature in projected)
        {
            MapFeature feature = projectedFeature.Feature;
            Geometry geometry = projectedFeature.Geometry;
            if (feature.Kind is TerrainKind.River or TerrainKind.Lake)
            {
                foreach (Geometry opening in waterOpenings.Query(geometry.EnvelopeInternal))
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

                Index.Insert(corridor.EnvelopeInternal, new(
                    feature.Id,
                    feature.Kind,
                    corridor,
                    PreparedGeometryFactory.Prepare(corridor)));
                continue;
            }

            if (!geometry.IsEmpty)
            {
                Index.Insert(geometry.EnvelopeInternal, new(
                    feature.Id,
                    feature.Kind,
                    geometry,
                    PreparedGeometryFactory.Prepare(geometry)));
            }
        }

        Index.Build();

        TransitionPortals = bridgePortals.Concat(GasCrossings.Portals).ToArray();
    }
}
