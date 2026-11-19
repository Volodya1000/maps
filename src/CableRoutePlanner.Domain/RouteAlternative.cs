namespace CableRoutePlanner.Domain;

public sealed record RouteAlternative(
    Guid Id,
    string Name,
    IReadOnlyList<GeoCoordinate> Geometry,
    RouteMetrics Metrics,
    IReadOnlyList<GeoCoordinate>? TransitionPoints = null,
    IReadOnlyList<RouteRoadContact>? RoadContacts = null,
    IReadOnlyList<long>? GasPipelineCandidateIds = null,
    IReadOnlyList<long>? UnderpassCandidateIds = null,
    IReadOnlyList<RoadCrossingEvent>? RoadCrossingEvents = null,
    IReadOnlyList<UnderpassPassage>? UnderpassPassages = null);
