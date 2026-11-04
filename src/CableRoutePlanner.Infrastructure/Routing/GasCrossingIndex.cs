using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.IO;

namespace CableRoutePlanner.Infrastructure.Routing;

internal sealed class GasCrossingIndex
{
    private const double AlignmentToleranceMetres = 1;
    // A review window around the road axis, not a surveyed carriageway width.
    private const double RoadReviewHalfWidthMetres = 3;
    private sealed record Candidate(long Id, Geometry Span, IPreparedGeometry Corridor);
    private readonly STRtree<Candidate> _index = new();
    public IReadOnlyList<GridPortal> Portals { get; }
    public bool HasCandidates => Portals.Count > 0;

    public GasCrossingIndex(IReadOnlyList<MapFeature> features, LocalProjection projection)
    {
        List<GridPortal> portals = [];
        var reader = new WKBReader();
        foreach (MapFeature feature in features.Where(feature => feature.Kind == TerrainKind.GasPipelineCrossingCandidate))
        {
            Geometry span = projection.Forward(reader.Read(feature.Wkb));
            if (span is not LineString || span.IsEmpty)
            { continue; }
            Geometry corridor = span.Buffer(AlignmentToleranceMetres);
            _index.Insert(corridor.EnvelopeInternal, new Candidate(feature.Id, span, PreparedGeometryFactory.Prepare(corridor)));
            portals.Add(new GridPortal(span.Coordinates));
        }
        _index.Build();
        Portals = portals.AsReadOnly();
    }

    public IReadOnlyList<long> UsedBy(LineString segment, Geometry road, Geometry intersection, Point crossing)
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
