using CableRoutePlanner.Domain;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;

namespace CableRoutePlanner.Infrastructure.Geography;

/// <summary>Approximates geographic coordinates in local metres using a fixed longitude scale at the origin.</summary>
/// <remarks>
/// There is no enforced 20 km limit. Distortion grows across large latitude spans;
/// polar origins and antimeridian wrapping are not specially handled. Search uses
/// this approximation while final segment lengths are measured geodesically.
/// </remarks>
public sealed class LocalProjection(GeoCoordinate origin)
{
    private const double MetresPerDegree = GeoCoordinate.MeanEarthRadiusMetres * Math.PI / 180;

    private readonly double _longitudeScale = MetresPerDegree * Math.Cos(origin.Latitude * Math.PI / 180);

    public Coordinate Forward(GeoCoordinate p) => new(
        (p.Longitude - origin.Longitude) * _longitudeScale,
        (p.Latitude - origin.Latitude) * MetresPerDegree);

    public GeoCoordinate Inverse(Coordinate c) => new(
        origin.Latitude + c.Y / MetresPerDegree,
        origin.Longitude + c.X / _longitudeScale);

    public Geometry Forward(Geometry geometry) => new GeometryTransformerImpl(this).Transform(geometry);

    private sealed class GeometryTransformerImpl(LocalProjection projection) : GeometryTransformer
    {
        protected override CoordinateSequence TransformCoordinates(CoordinateSequence coords, Geometry parent) => Factory.CoordinateSequenceFactory.Create(
            coords.ToCoordinateArray().Select(c => projection.Forward(new GeoCoordinate(c.Y, c.X))).ToArray());
    }
}
