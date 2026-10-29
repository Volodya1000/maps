namespace CableRoutePlanner.Domain;

public enum RoadContactKind { Crossing, SharedSection }

/// <summary>Contact with a mapped road axis/area; not a construction approval.</summary>
public sealed record RouteRoadContact(long RoadId, RoadContactKind Kind, IReadOnlyList<GeoCoordinate> Geometry);
