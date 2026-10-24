using System.Globalization;
using System.Runtime.InteropServices;

namespace CableRoutePlanner.Domain;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct GeoCoordinate
{
    public const double MeanEarthRadiusMetres = 6_371_008.8;

    public double Latitude
    {
        get;
    }
    public double Longitude
    {
        get;
    }

    public GeoCoordinate(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || latitude is < -90 or > 90
            || !double.IsFinite(longitude) || longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), "Некорректные географические координаты.");
        }

        Latitude = latitude;
        Longitude = longitude;
    }

    public double DistanceTo(GeoCoordinate other)
    {
        const double radians = Math.PI / 180;
        var dLat = (other.Latitude - Latitude) * radians;
        var dLon = (other.Longitude - Longitude) * radians;
        var a = Math.Pow(Math.Sin(dLat / 2), 2)
            + Math.Cos(Latitude * radians) * Math.Cos(other.Latitude * radians)
            * Math.Pow(Math.Sin(dLon / 2), 2);
        return MeanEarthRadiusMetres * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
    }

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"{Latitude:F6}, {Longitude:F6}");
}
