namespace CableRoutePlanner.Domain;

/// <summary>Classifies the mapped method used by one actual road-crossing event.</summary>
public enum RoadCrossingKind
{
    OverheadGasPipeline,
    PedestrianUnderpass,
    Bridge
}

/// <summary>Retains the route-local evidence for one physical road crossing.</summary>
public sealed record RoadCrossingEvent(long RoadId, RoadCrossingKind Kind, GeoCoordinate Location);
