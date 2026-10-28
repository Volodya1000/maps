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
    {
        _projection = projection;
        var reader = new WKBReader();
        foreach (MapFeature feature in features.Where(feature =>
            feature.Kind is TerrainKind.Road or TerrainKind.BridgeCandidate))
        {
            Geometry geometry = projection.Forward(reader.Read(feature.Wkb));
            _index.Insert(geometry.EnvelopeInternal, new Road(feature.Id, geometry));
        }
        _index.Build();
    }

    public IReadOnlyList<RouteRoadContact> Analyze(IReadOnlyList<Coordinate> route)
    {
        if (route.Count < 2)
        { return []; }
        LineString line = new(route.ToArray());
        List<RouteRoadContact> contacts = [];
        foreach (Road road in _index.Query(line.EnvelopeInternal).OrderBy(road => road.Id))
        {
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
