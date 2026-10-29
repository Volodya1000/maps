using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.IO;

namespace CableRoutePlanner.Infrastructure.Routing;

// Reviews the full final route, independently of grid edges, viewport and display LOD.
internal sealed class RoadContactAnalyzer
{
    private sealed record Road(long Id, Geometry Geometry);
    private readonly STRtree<Road> _index = new();
    private readonly LocalProjection _projection;

    public RoadContactAnalyzer(IReadOnlyList<MapFeature> features, LocalProjection projection)
        : this(ProjectRoads(features, projection), projection) { }

    public RoadContactAnalyzer(IEnumerable<(long Id, Geometry Geometry)> roads, LocalProjection projection)
    {
        _projection = projection;
        foreach ((long id, Geometry geometry) in roads)
        {
            _index.Insert(geometry.EnvelopeInternal, new Road(id, geometry));
        }
        _index.Build();
    }

    private static IEnumerable<(long Id, Geometry Geometry)> ProjectRoads(IReadOnlyList<MapFeature> features, LocalProjection projection)
    {
        var reader = new WKBReader();
        return features.Where(feature => feature.Kind is TerrainKind.Road or TerrainKind.BridgeCandidate)
            .Select(feature => (feature.Id, projection.Forward(reader.Read(feature.Wkb))));
    }

    public IReadOnlyList<RouteRoadContact> Analyze(IReadOnlyList<Coordinate> route, CancellationToken cancellationToken = default)
    {
        if (route.Count < 2)
        { return []; }
        LineString line = new(route.ToArray());
        List<RouteRoadContact> contacts = [];
        Dictionary<long, Road> candidates = [];
        for (int i = 1; i < route.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Envelope segmentBounds = new(route[i - 1], route[i]);
            foreach (Road road in _index.Query(segmentBounds))
            { candidates.TryAdd(road.Id, road); }
        }
        foreach (Road road in candidates.Values.OrderBy(road => road.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Append(road.Geometry.Intersection(line), road.Id);
        }
        return contacts.AsReadOnly();

        void Append(Geometry geometry, long roadId)
        {
            if (geometry.IsEmpty)
            { return; }
            switch (geometry)
            {
                case GeometryCollection collection:
                    foreach (Geometry part in collection.Geometries)
                    { Append(part, roadId); }
                    break;
                case Point point:
                    AddContact(point, RoadContactKind.Crossing);
                    break;
                case LineString shared:
                    AddContact(shared, RoadContactKind.SharedSection);
                    break;
            }

            void AddContact(Geometry contact, RoadContactKind kind) => contacts.Add(new RouteRoadContact(
                roadId, kind, Array.AsReadOnly(contact.Coordinates.Select(_projection.Inverse).ToArray())));
        }
    }
}
