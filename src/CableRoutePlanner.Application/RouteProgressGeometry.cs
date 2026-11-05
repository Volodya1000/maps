using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CableRoutePlanner.Domain;

namespace CableRoutePlanner.Application;

public static class RouteProgressGeometry
{
    public static double CalculateLength(IReadOnlyList<GeoCoordinate> geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        double total = 0;
        for (int index = 0; index < geometry.Count - 1; index++)
        {
            total += geometry[index].DistanceTo(geometry[index + 1]);
        }

        return total;
    }

    public static bool Matches(
        IReadOnlyList<GeoCoordinate> left,
        IReadOnlyList<GeoCoordinate> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First == pair.Second);

    public static string Fingerprint(IReadOnlyList<GeoCoordinate> geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        StringBuilder text = new();
        foreach (GeoCoordinate coordinate in geometry)
        {
            text.Append(coordinate.Latitude.ToString("R", CultureInfo.InvariantCulture));
            text.Append(',');
            text.Append(coordinate.Longitude.ToString("R", CultureInfo.InvariantCulture));
            text.Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
