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

    public IReadOnlyList<long> UsedBy(LineString segment, Geometry road)
    {
        if (!HasCandidates)
        { return []; }
        List<long> used = [];
        foreach (Candidate candidate in _index.Query(segment.EnvelopeInternal))
        {
            if (!candidate.Span.Crosses(road))
            { continue; }
            Geometry reviewSection = segment.Intersection(road.Buffer(RoadReviewHalfWidthMetres));
            if (!reviewSection.IsEmpty && candidate.Corridor.Covers(reviewSection))
            { used.Add(candidate.Id); }
        }
        return used;
    }
}
