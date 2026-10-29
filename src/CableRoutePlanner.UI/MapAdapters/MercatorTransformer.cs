using Mapsui.Projections;
using CableRoutePlanner.Domain;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;

namespace CableRoutePlanner.UI.MapAdapters;

internal sealed class MercatorTransformer : GeometryTransformer
{
    protected override CoordinateSequence TransformCoordinates(CoordinateSequence coords, Geometry parent)
    {
        CoordinateSequence projected = Factory.CoordinateSequenceFactory.Create(coords.Count, 2, 0);
        for (int index = 0; index < coords.Count; index++)
        {
            GeoCoordinate coordinate = new(coords.GetY(index), coords.GetX(index));
            (double x, double y) = SphericalMercator.FromLonLat(coordinate.Longitude, coordinate.Latitude);
            projected.SetX(index, x);
            projected.SetY(index, y);
        }
        return projected;
    }
}
