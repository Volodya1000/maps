namespace CableRoutePlanner.Domain;

// The polygon describes available package data, not engineering approval or a
// legal border. Rectangular Bounds alone do not establish coverage of an extract.
public sealed class GeoCoverage
{
    private const double BoundaryToleranceDegrees = 1e-10;
    private readonly GeoBounds _bounds;
    public IReadOnlyList<GeoCoordinate> Boundary { get; }

    public GeoCoverage(IEnumerable<GeoCoordinate> boundary)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        GeoCoordinate[] points = boundary.ToArray();
        if (points.Length < 4)
        {
            throw new ArgumentException($"Coverage boundary has {points.Length} coordinates; at least four are required.", nameof(boundary));
        }
        if (points[0] != points[^1])
        {
            throw new ArgumentException($"Coverage boundary is open: first={points[0]}, last={points[^1]}; the coordinates must match.", nameof(boundary));
        }
        double area = 0;
        for (int i = 0; i < points.Length - 1; i++)
        {
            if (points[i] == points[i + 1])
            {
                throw new ArgumentException($"Coverage boundary has a zero-length edge at {i}.", nameof(boundary));
            }
            area += Cross(points[0], points[i], points[i + 1]);
            for (int j = i + 2; j < points.Length - 1; j++)
            {
                if (i == 0 && j == points.Length - 2)
                {
                    continue;
                }
                if (Intersects(points[i], points[i + 1], points[j], points[j + 1]))
                {
                    throw new ArgumentException($"Coverage boundary edges {i} and {j} intersect.", nameof(boundary));
                }
            }
        }
        if (area == 0)
        {
            throw new ArgumentException("Coverage boundary has zero area.", nameof(boundary));
        }
        Boundary = Array.AsReadOnly(points);
        _bounds = new(points.Min(p => p.Longitude), points.Min(p => p.Latitude),
            points.Max(p => p.Longitude), points.Max(p => p.Latitude));
    }

    public bool Contains(GeoCoordinate point)
    {
        if (!_bounds.Contains(point))
        {
            return false;
        }
        bool inside = false;
        for (int i = 1; i < Boundary.Count; i++)
        {
            GeoCoordinate a = Boundary[i - 1];
            GeoCoordinate b = Boundary[i];
            if (OnSegment(a, b, point))
            {
                return true;
            }
            if ((a.Latitude > point.Latitude) != (b.Latitude > point.Latitude)
                && point.Longitude < a.Longitude + (point.Latitude - a.Latitude)
                    * (b.Longitude - a.Longitude) / (b.Latitude - a.Latitude))
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private static double Cross(GeoCoordinate origin, GeoCoordinate first, GeoCoordinate second) =>
        (first.Longitude - origin.Longitude) * (second.Latitude - origin.Latitude)
        - (first.Latitude - origin.Latitude) * (second.Longitude - origin.Longitude);

    private static bool OnSegment(GeoCoordinate a, GeoCoordinate b, GeoCoordinate p) =>
        Math.Abs(Cross(a, b, p)) <= BoundaryToleranceDegrees
            * (Math.Abs(b.Longitude - a.Longitude) + Math.Abs(b.Latitude - a.Latitude))
        && p.Longitude >= Math.Min(a.Longitude, b.Longitude) - BoundaryToleranceDegrees
        && p.Longitude <= Math.Max(a.Longitude, b.Longitude) + BoundaryToleranceDegrees
        && p.Latitude >= Math.Min(a.Latitude, b.Latitude) - BoundaryToleranceDegrees
        && p.Latitude <= Math.Max(a.Latitude, b.Latitude) + BoundaryToleranceDegrees;

    private static bool Intersects(GeoCoordinate a, GeoCoordinate b, GeoCoordinate c, GeoCoordinate d) =>
        OnSegment(a, b, c) || OnSegment(a, b, d) || OnSegment(c, d, a) || OnSegment(c, d, b)
        || Math.Sign(Cross(a, b, c)) != Math.Sign(Cross(a, b, d))
            && Math.Sign(Cross(c, d, a)) != Math.Sign(Cross(c, d, b));
}
