using CableRoutePlanner.Domain;
namespace CableRoutePlanner.Application;

// Geometry is portable WKB in EPSG:4326; neither Domain nor ViewModels reference a GIS library.
public sealed record MapFeature(long Id, TerrainKind Kind, byte[] Wkb, string Name, string? ApprovalId = null);
public sealed record DataPackageInfo(string RegionId, GeoBounds Bounds, string Attribution, bool HasTerrain, string Status);
public interface IGeodataRepository
{
    DataPackageInfo Info { get; }
    Task<IReadOnlyList<MapFeature>> QueryAsync(GeoBounds bounds, CancellationToken cancellationToken);
}
public interface IRoutePlanner
{
    Task<IReadOnlyList<RouteAlternative>> PlanAsync(IReadOnlyList<GeoCoordinate> points,
        RoutingOptions options, CancellationToken cancellationToken);
}
public interface IClipboardService { Task SetTextAsync(string text); }
public interface ISettingsStore
{
    RoutingOptions Load();
    void Save(RoutingOptions options);
}
public sealed class PlanningException(string message) : Exception(message);
public interface IPlanningSession
{
    IReadOnlyList<Waypoint> Points { get; }
    long Revision { get; }
    Waypoint Add(GeoCoordinate coordinate);
    void Move(Guid id, GeoCoordinate coordinate);
    void Remove(Guid id);
    void Reorder(int oldIndex, int newIndex);
    void Clear();
    Task<IReadOnlyList<RouteAlternative>> BuildAsync(RoutingOptions options, CancellationToken cancellationToken);
}
