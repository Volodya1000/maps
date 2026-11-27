using CableRoutePlanner.Domain;

namespace CableRoutePlanner.Infrastructure.Elevation;

/// <summary>Measures the route polyline in the local metric of the WGS84 ellipsoid.</summary>
internal static class Wgs84Polyline
{
    // NGA WGS84 defining parameters. The route vertices describe a geographic polyline.
    private const double EquatorialRadiusMetres = 6_378_137;
    private const double Flattening = 1 / 298.257223563;
    private const double EccentricitySquared = Flattening * (2 - Flattening);
    private const double MaximumCurvatureRadiusMetres = EquatorialRadiusMetres / (1 - Flattening);
    private const double RadiansPerDegree = Math.PI / 180;
    private const double IntegrationPanelDegrees = 1;

    public static double SegmentLength(GeoCoordinate start, GeoCoordinate end)
    {
        double latitudeRadians = start.Latitude * RadiansPerDegree;
        double latitudeDeltaDegrees = end.Latitude - start.Latitude;
        double latitudeDeltaRadians = latitudeDeltaDegrees * RadiansPerDegree;
        double longitudeDeltaRadians = LongitudeDelta(start, end) * RadiansPerDegree;
        if (latitudeDeltaDegrees == 0)
        {
            return Speed(latitudeRadians, latitudeDeltaRadians, longitudeDeltaRadians);
        }

        // Integrate the differential arc length along the same straight coordinate
        // interpolation used for the DEM samples. Small intervals need only one panel.
        int panels = (int)Math.Ceiling(Math.Abs(latitudeDeltaDegrees) / IntegrationPanelDegrees);
        int intervals = 2 * panels;
        double sum = Speed(latitudeRadians, latitudeDeltaRadians, longitudeDeltaRadians)
            + Speed(latitudeRadians + latitudeDeltaRadians, latitudeDeltaRadians, longitudeDeltaRadians);
        for (int index = 1; index < intervals; index++)
        {
            double latitude = latitudeRadians + latitudeDeltaRadians * index / intervals;
            sum += (index % 2 == 0 ? 2 : 4) * Speed(latitude, latitudeDeltaRadians, longitudeDeltaRadians);
        }

        return sum / (6 * panels);
    }

    public static double SegmentLengthUpperBound(GeoCoordinate start, GeoCoordinate end)
    {
        double latitudeDelta = end.Latitude - start.Latitude;
        double closestLatitudeToEquator = Math.Clamp(0, Math.Min(start.Latitude, end.Latitude), Math.Max(start.Latitude, end.Latitude));
        double parallelDelta = LongitudeDelta(start, end) * CosLatitude(closestLatitudeToEquator * RadiansPerDegree);
        return MaximumCurvatureRadiusMetres * RadiansPerDegree
            * Math.Sqrt(latitudeDelta * latitudeDelta + parallelDelta * parallelDelta);
    }

    public static GeoCoordinate Interpolate(GeoCoordinate start, GeoCoordinate end, double fraction) => new(
        start.Latitude + (end.Latitude - start.Latitude) * fraction,
        Math.IEEERemainder(start.Longitude + LongitudeDelta(start, end) * fraction, 360));

    private static double LongitudeDelta(GeoCoordinate start, GeoCoordinate end) =>
        Math.IEEERemainder(end.Longitude - start.Longitude, 360);

    private static double CosLatitude(double latitudeRadians) =>
        Math.Abs(latitudeRadians) >= Math.PI / 2 ? 0 : Math.Cos(latitudeRadians);

    private static double Speed(double latitudeRadians, double latitudeDeltaRadians, double longitudeDeltaRadians)
    {
        double sine = Math.Sin(latitudeRadians);
        double normalFactor = 1 - EccentricitySquared * sine * sine;
        double primeVerticalRadius = EquatorialRadiusMetres / Math.Sqrt(normalFactor);
        double meridianRadius = primeVerticalRadius * (1 - EccentricitySquared) / normalFactor;
        double north = meridianRadius * latitudeDeltaRadians;
        double east = primeVerticalRadius * CosLatitude(latitudeRadians) * longitudeDeltaRadians;
        return Math.Sqrt(north * north + east * east);
    }
}
