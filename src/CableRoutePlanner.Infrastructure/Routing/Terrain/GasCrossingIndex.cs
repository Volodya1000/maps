using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.LinearReferencing;

namespace CableRoutePlanner.Infrastructure.Routing;

internal sealed class GasCrossingIndex
{
    private const double AlignmentToleranceMetres = 1;
    // A review window around the road axis, not a surveyed carriageway width.
    private const double RoadReviewHalfWidthMetres = 3;
    private const double UnderpassPortalHalfLengthMetres = 8;
    private const double MinimumUnderpassPortalHalfLengthMetres = 3.5;
    private sealed record Candidate(long Id, Geometry Span, IPreparedGeometry Corridor, IReadOnlyList<long>? RoadIds, IPreparedGeometry LocalCorridor, bool HasTopology);
    private readonly STRtree<Candidate> _index = new();
    private readonly List<(long Id, long Network, Geometry Corridor)> _underpassSpans = [];
    private readonly HashSet<(double X, double Y)> _networkVertices = [];
    public bool IsNetworkVertex(Coordinate coordinate) => _networkVertices.Contains((coordinate.X, coordinate.Y));
    public IReadOnlyList<GridPortal> Portals { get; }
    public bool HasCandidates => Portals.Count > 0;

    public GasCrossingIndex(IEnumerable<ProjectedMapFeature> features, TerrainKind kind = TerrainKind.GasPipelineCrossingCandidate)
    {
        ProjectedMapFeature[] projected = features.ToArray();
        Dictionary<long, Geometry> roads = projected.Where(feature => feature.Feature.Kind == TerrainKind.Road)
            .ToDictionary(feature => feature.Feature.Id, feature => feature.Geometry);
        Geometry[] blocked = projected.Where(feature => TerrainTraversalPolicy.For(feature.Feature.Kind) == TerrainTraversalRule.Blocked)
            .Select(feature => feature.Geometry).ToArray();
        Dictionary<long, IPreparedGeometry> networkCorridors = projected
            .Where(feature => feature.Feature.Kind == TerrainKind.PedestrianUnderpassCandidate && feature.Feature.UnderpassTopology is not null)
            .GroupBy(feature => feature.Feature.UnderpassTopology!.NetworkId)
            .ToDictionary(group => group.Key, group => PreparedGeometryFactory.Prepare(
                NetTopologySuite.Operation.Union.UnaryUnionOp.Union(group.Select(feature => feature.Geometry.Buffer(AlignmentToleranceMetres)).ToArray())));
        List<GridPortal> portals = [];
        foreach (ProjectedMapFeature feature in projected.Where(feature => feature.Feature.Kind == kind))
        {
            Geometry span = feature.Geometry;
            if (span is not LineString || span.IsEmpty)
            { continue; }
            List<GridPortal> candidatePortals = [];
            List<long> portalRoadIds = [];
            if (kind == TerrainKind.PedestrianUnderpassCandidate)
            {
                if (feature.Feature.UnderpassTopology is { } topology)
                {
                    foreach (Coordinate coordinate in span.Coordinates)
                    {
                        _networkVertices.Add((coordinate.X, coordinate.Y));
                    }
                    // Keep all original network vertices. Every segment is still
                    // evaluated against buildings, water and exact road links.
                    candidatePortals.Add(new GridPortal(span.Coordinates, topology));
                    portalRoadIds.AddRange(feature.Feature.UnderpassRoadIds ?? []);
                }
                else
                {
                    foreach (long roadId in feature.Feature.UnderpassRoadIds ?? [])
                    {
                        if (roads.TryGetValue(roadId, out Geometry? road))
                        {
                            GridPortal? portal = CreateUnderpassPortal((LineString)span, road, blocked);
                            if (portal is not null)
                            {
                                candidatePortals.Add(portal);
                                portalRoadIds.Add(roadId);
                            }
                        }
                    }
                }
            }
            else
            {
                candidatePortals.Add(new GridPortal(span.Coordinates));
            }
            if (candidatePortals.Count == 0)
            {
                continue;
            }

            Geometry corridor = span.Buffer(AlignmentToleranceMetres);
            if (kind == TerrainKind.PedestrianUnderpassCandidate)
            {
                _underpassSpans.Add((feature.Feature.Id, feature.Feature.UnderpassTopology?.NetworkId ?? feature.Feature.Id, corridor));
            }
            IReadOnlyList<long>? linkedRoads = kind == TerrainKind.PedestrianUnderpassCandidate
                ? portalRoadIds : feature.Feature.UnderpassRoadIds;
            IPreparedGeometry localCorridor = PreparedGeometryFactory.Prepare(corridor);
            IPreparedGeometry reviewCorridor = feature.Feature.UnderpassTopology is { } network
                ? networkCorridors[network.NetworkId] : localCorridor;
            _index.Insert(corridor.EnvelopeInternal, new Candidate(feature.Feature.Id, span, reviewCorridor, linkedRoads, localCorridor, feature.Feature.UnderpassTopology is not null));
            portals.AddRange(candidatePortals);
        }
        _index.Build();
        Portals = portals.AsReadOnly();
    }

    public IReadOnlyList<UnderpassPassage> Passages(IReadOnlyList<Coordinate> route, IReadOnlySet<long> usedIds, LocalProjection projection)
    {
        List<UnderpassPassage> result = [];
        LineString path = new(route.ToArray());
        LengthIndexedLine indexed = new(path);
        foreach (IGrouping<long, (long Id, long Network, Geometry Corridor)> network in _underpassSpans.GroupBy(span => span.Network))
        {
            if (!network.Any(span => usedIds.Contains(span.Id)))
            {
                continue;
            }
            // Parameter intervals along the route preserve repeated use of the
            // same structure, even when the path retraces identical coordinates.
            Geometry corridor = NetTopologySuite.Operation.Union.UnaryUnionOp.Union(network.Select(span => span.Corridor).ToArray());
            double travelled = 0;
            List<(double Start, double End)> intervals = [];
            for (int i = 1; i < route.Count; i++)
            {
                LineString edge = new([route[i - 1], route[i]]);
                Geometry inside = edge.Intersection(corridor);
                foreach (LineString section in Lines(inside))
                {
                    double start = travelled + route[i - 1].Distance(section.StartPoint.Coordinate);
                    double end = travelled + route[i - 1].Distance(section.EndPoint.Coordinate);
                    intervals.Add((Math.Min(start, end), Math.Max(start, end)));
                }
                travelled += edge.Length;
            }
            double? entry = null;
            double exit = 0;
            foreach ((double start, double end) in intervals.OrderBy(interval => interval.Start))
            {
                if (entry.HasValue && start > exit + PassageJoinToleranceMetres)
                {
                    AddPassage(entry.Value, exit);
                    entry = null;
                }
                entry ??= start;
                exit = Math.Max(exit, end);
            }
            if (entry.HasValue)
            {
                AddPassage(entry.Value, exit);
            }

            void AddPassage(double start, double end) => result.Add(new(network.Key,
                projection.Inverse(indexed.ExtractPoint(start)), projection.Inverse(indexed.ExtractPoint(end))));
        }
        return result.AsReadOnly();
    }

    private const double PassageJoinToleranceMetres = 0.001;

    private static IEnumerable<LineString> Lines(Geometry geometry) => geometry switch
    {
        LineString line when line.Length > 0 => [line],
        GeometryCollection collection => collection.Geometries.SelectMany(Lines),
        _ => []
    };

    private static GridPortal? CreateUnderpassPortal(LineString tunnel, Geometry road, Geometry[] blocked)
    {
        if (tunnel.Intersection(road) is not Point crossing)
        {
            return null;
        }

        LengthIndexedLine indexed = new(tunnel);
        double center = indexed.Project(crossing.Coordinate);
        double left = FindClearEndpoint(indexed, center, -1, blocked);
        double right = FindClearEndpoint(indexed, center, 1, blocked);
        if (center - left < MinimumUnderpassPortalHalfLengthMetres
            || right - center < MinimumUnderpassPortalHalfLengthMetres)
        {
            return null;
        }

        LineString passage = (LineString)indexed.ExtractLine(left, right);
        return passage.Crosses(road) ? new GridPortal(passage.Coordinates) : null;
    }

    private static double FindClearEndpoint(LengthIndexedLine indexed, double center, int direction, Geometry[] blocked)
    {
        double limit = direction < 0 ? indexed.StartIndex : indexed.EndIndex;
        double distance = Math.Min(UnderpassPortalHalfLengthMetres, Math.Abs(limit - center));
        for (; distance >= MinimumUnderpassPortalHalfLengthMetres; distance -= 0.5)
        {
            double endpoint = center + direction * distance;
            Geometry approach = indexed.ExtractLine(Math.Min(center, endpoint), Math.Max(center, endpoint));
            if (!blocked.Any(obstacle => obstacle.Intersects(approach)))
            {
                return endpoint;
            }
        }

        return center;
    }

    public IReadOnlyList<long> UsedBy(LineString segment, Geometry road, Geometry intersection, Point crossing, long? requiredRoadId = null, bool includeNetworkCandidates = true)
    {
        if (!HasCandidates)
        { return []; }
        List<long> used = [];
        IList<Candidate> candidates = _index.Query(crossing.EnvelopeInternal);
        if (candidates.Count == 0)
        { return []; }
        LineString localSegment = ReviewSegment(segment, intersection, crossing);
        Geometry reviewSection = localSegment.Intersection(road.Buffer(RoadReviewHalfWidthMetres));
        Geometry[] sections = reviewSection is GeometryCollection collection
            ? collection.Geometries : [reviewSection];
        Geometry[] crossingSections = sections.Where(section => section.Covers(crossing)).ToArray();
        foreach (Candidate candidate in candidates)
        {
            if (candidate.HasTopology && !includeNetworkCandidates)
            { continue; }
            if (requiredRoadId.HasValue && !(candidate.RoadIds?.Contains(requiredRoadId.Value) ?? false))
            { continue; }
            // Topology-backed links also retain crossings split between ways
            // at their shared OSM node. Legacy candidates still need a single
            // transverse source span; local and network corridors remain required.
            if (!candidate.HasTopology && !candidate.Span.Crosses(road))
            { continue; }
            if (crossingSections.Length > 0 && candidate.LocalCorridor.Covers(crossing)
                && crossingSections.All(candidate.Corridor.Covers))
            { used.Add(candidate.Id); }
        }
        return used;
    }

    private static LineString ReviewSegment(LineString segment, Geometry intersection, Point crossing)
    {
        double length = segment.Length;
        if (length == 0)
        { return segment; }
        Coordinate start = segment.GetCoordinateN(0);
        Coordinate end = segment.GetCoordinateN(1);
        double distance = start.Distance(crossing.Coordinate);
        double lower = 0;
        double upper = length;
        // Neighbouring point contacts or shared-section endpoints bound this
        // crossing's review region. Merged road buffers must not couple them.
        foreach (Coordinate contact in intersection.Coordinates)
        {
            double other = start.Distance(contact);
            if (other < distance)
            { lower = Math.Max(lower, (other + distance) / 2); }
            else if (other > distance)
            { upper = Math.Min(upper, (other + distance) / 2); }
        }
        return segment.Factory.CreateLineString([At(lower), crossing.Coordinate, At(upper)]);

        Coordinate At(double offset) => new(
            start.X + (end.X - start.X) * offset / length,
            start.Y + (end.Y - start.Y) * offset / length);
    }
}
