using CableRoutePlanner.Domain;

namespace CableRoutePlanner.Application;

// Geometry is portable WKB in EPSG:4326; neither Domain nor ViewModels reference a GIS library.
public sealed record MapFeature(long Id, TerrainKind Kind, byte[] Wkb, string Name, string? ApprovalId = null);

public sealed record DataPackageInfo(
    string RegionId,
    GeoBounds Bounds,
    string Attribution,
    bool HasTerrain,
    string Status);

public interface IGeodataRepository
{
    DataPackageInfo Info { get; }

    Task<IReadOnlyList<MapFeature>> QueryAsync(GeoBounds bounds, CancellationToken cancellationToken);

    Task<IReadOnlyList<MapFeature>> QueryMapAsync(
        GeoBounds bounds,
        double metresPerPixel,
        CancellationToken cancellationToken) => QueryAsync(bounds, cancellationToken);
}

public interface IRoutePlanner
{
    Task<IReadOnlyList<RouteAlternative>> PlanAsync(
        IReadOnlyList<GeoCoordinate> points,
        RoutingOptions options,
        CancellationToken cancellationToken);
}

public interface IPointPlacementValidator
{
    Task ValidateAsync(GeoCoordinate coordinate, CancellationToken cancellationToken);
}

public interface ISettingsStore
{
    RoutingOptions Load();

    void Save(RoutingOptions options);
}

public sealed class PlanningException : Exception
{
    public PlanningException()
    {
    }

    public PlanningException(string message)
        : base(message)
    {
    }

    public PlanningException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public interface IPlanningSession
{
    IReadOnlyList<Waypoint> Points { get; }

    long Revision { get; }

    Task<Waypoint> AddAsync(GeoCoordinate coordinate, CancellationToken cancellationToken);

    Task MoveAsync(Guid id, GeoCoordinate coordinate, CancellationToken cancellationToken);

    void Remove(Guid id);

    void Reorder(int oldIndex, int newIndex);

    void Clear();

    Task<IReadOnlyList<RouteAlternative>> BuildAsync(RoutingOptions options, CancellationToken cancellationToken);
}
