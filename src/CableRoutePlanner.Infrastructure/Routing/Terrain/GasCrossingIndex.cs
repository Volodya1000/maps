using CableRoutePlanner.Domain;
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
    private sealed record Candidate(long Id, Geometry Span, IPreparedGeometry Corridor, IReadOnlyList<long>? RoadIds);
    private readonly STRtree<Candidate> _index = new();
    public IReadOnlyList<GridPortal> Portals { get; }
    public bool HasCandidates => Portals.Count > 0;

    public GasCrossingIndex(IEnumerable<ProjectedMapFeature> features, TerrainKind kind = TerrainKind.GasPipelineCrossingCandidate)
    {
        ProjectedMapFeature[] projected = features.ToArray();
        Dictionary<long, Geometry> roads = projected.Where(feature => feature.Feature.Kind == TerrainKind.Road)
            .ToDictionary(feature => feature.Feature.Id, feature => feature.Geometry);
        Geometry[] blocked = projected.Where(feature => TerrainTraversalPolicy.For(feature.Feature.Kind) == TerrainTraversalRule.Blocked)
            .Select(feature => feature.Geometry).ToArray();
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
            else
            {
                candidatePortals.Add(new GridPortal(span.Coordinates));
            }
            if (candidatePortals.Count == 0)
            {
                continue;
            }

            Geometry corridor = span.Buffer(AlignmentToleranceMetres);
            IReadOnlyList<long>? linkedRoads = kind == TerrainKind.PedestrianUnderpassCandidate
                ? portalRoadIds : feature.Feature.UnderpassRoadIds;
            _index.Insert(corridor.EnvelopeInternal, new Candidate(feature.Feature.Id, span, PreparedGeometryFactory.Prepare(corridor), linkedRoads));
            portals.AddRange(candidatePortals);
        }
        _index.Build();
        Portals = portals.AsReadOnly();
    }

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

    public IReadOnlyList<long> UsedBy(LineString segment, Geometry road, Geometry intersection, Point crossing, long? requiredRoadId = null)
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
            if (requiredRoadId.HasValue && !(candidate.RoadIds?.Contains(requiredRoadId.Value) ?? false))
            { continue; }
            if (!candidate.Span.Crosses(road))
            { continue; }
            if (crossingSections.Length > 0 && candidate.Corridor.Covers(crossing)
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
