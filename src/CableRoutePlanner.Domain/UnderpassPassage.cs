namespace CableRoutePlanner.Domain;

/// <summary>One continuous route visit to a mapped underground structure, independent of road contacts.</summary>
public sealed record UnderpassPassage(long NetworkId, GeoCoordinate Entry, GeoCoordinate Exit);
