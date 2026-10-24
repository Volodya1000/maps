using System.Runtime.InteropServices;

namespace CableRoutePlanner.Domain;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct GeoBounds(double West, double South, double East, double North)
{
    public bool Contains(GeoCoordinate point) => point.Longitude >= West
        && point.Longitude <= East
        && point.Latitude >= South
        && point.Latitude <= North;

    public bool Contains(GeoBounds bounds) => bounds.West >= West && bounds.East <= East
        && bounds.South >= South && bounds.North <= North;

    public static GeoBounds Around(IReadOnlyList<GeoCoordinate> points, double marginDegrees = 0.01) => new(
        points.Min(point => point.Longitude) - marginDegrees,
        points.Min(point => point.Latitude) - marginDegrees,
        points.Max(point => point.Longitude) + marginDegrees,
        points.Max(point => point.Latitude) + marginDegrees);
}
