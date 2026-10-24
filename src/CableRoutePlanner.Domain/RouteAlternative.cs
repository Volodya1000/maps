namespace CableRoutePlanner.Domain;

public sealed record RouteAlternative(
    Guid Id,
    string Name,
    IReadOnlyList<GeoCoordinate> Geometry,
    RouteMetrics Metrics);
