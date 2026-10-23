namespace CableRoutePlanner.Domain;

public readonly record struct GeoCoordinate
{
    // IUGG mean Earth radius (2a + b) / 3 for WGS 84, rounded to 0.1 m.
    // https://epsg.org/ellipsoid_7030/WGS-84.html — used for spherical haversine distances.
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
        if (!double.IsFinite(latitude)
            || latitude is < -90 or > 90
            || !double.IsFinite(longitude)
            || longitude is < -180 or > 180)
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

    public override string ToString() => FormattableString.Invariant($"{Latitude:F6}, {Longitude:F6}");
}

public readonly record struct GeoBounds(double West, double South, double East, double North)
{
    public bool Contains(GeoCoordinate p) => p.Longitude >= West
        && p.Longitude <= East
        && p.Latitude >= South
        && p.Latitude <= North;

    public bool Contains(GeoBounds b) => b.West >= West && b.East <= East && b.South >= South && b.North <= North;

    public static GeoBounds Around(
        IReadOnlyList<GeoCoordinate> points,
        double marginDegrees = 0.01) => new(
        points.Min(p => p.Longitude) - marginDegrees,
        points.Min(p => p.Latitude) - marginDegrees,
        points.Max(p => p.Longitude) + marginDegrees,
        points.Max(p => p.Latitude) + marginDegrees);
}

public sealed record Waypoint(Guid Id, GeoCoordinate Coordinate);

public enum TerrainKind
{
    Road,
    River,
    Lake,
    Marsh,
    Forest,
    Field,
    Clearing,
    Building,
    BridgeCandidate,
    ApprovedCrossing,
    Settlement
}
