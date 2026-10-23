using CableRoutePlanner.Domain;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
namespace CableRoutePlanner.Infrastructure.Geography;

// Local equidistant approximation, deliberately limited to a 20 km AOI by GridRoutePlanner.
internal sealed class LocalProjection(GeoCoordinate origin)
{
    private const double MetresPerDegree = 6371008.8 * Math.PI / 180;
    private readonly double _longitudeScale = MetresPerDegree * Math.Cos(origin.Latitude * Math.PI / 180);
    public Coordinate Forward(GeoCoordinate p) => new((p.Longitude - origin.Longitude) * _longitudeScale,
        (p.Latitude - origin.Latitude) * MetresPerDegree);
    public GeoCoordinate Inverse(Coordinate c) => new(origin.Latitude + c.Y / MetresPerDegree,
        origin.Longitude + c.X / _longitudeScale);
    public Geometry Forward(Geometry geometry) => new GeometryTransformerImpl(this).Transform(geometry);
    private sealed class GeometryTransformerImpl(LocalProjection projection) : GeometryTransformer
    {
        protected override CoordinateSequence TransformCoordinates(CoordinateSequence coords, Geometry parent) =>
            Factory.CoordinateSequenceFactory.Create(coords.ToCoordinateArray().Select(c =>
                projection.Forward(new GeoCoordinate(c.Y, c.X))).ToArray());
    }
}
