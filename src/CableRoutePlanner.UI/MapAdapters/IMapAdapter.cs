using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.ViewModels;

namespace CableRoutePlanner.UI.MapAdapters;

public interface IMapAdapter : IDisposable
{
    event Action<GeoBounds, double>? ViewportChanged;

    event Action<GeoCoordinate>? PointAdded;

    event Action<Guid, GeoCoordinate>? PointMoved;

    event Action<Guid>? WaypointSelected;

    event Action<double>? NorthIndicatorRotationChanged;

    event Action<int>? RouteVertexSelected;

    void InitializeViewport();

    Task SetFeaturesAsync(IReadOnlyList<MapFeature> features);

    void SetPoints(IReadOnlyList<WaypointRow> points);

    void SetRouteVertices(IReadOnlyList<RouteVertexRow> vertices, bool showAll = false);

    void SetRoutes(IReadOnlyList<RouteAlternative> routes, Guid? selectedId);

    void SetRoadContacts(IReadOnlyList<RouteRoadContact> contacts);

    void SetAddMode(bool enabled);

    void ZoomIn();

    void ZoomOut();

    void ResetNorth();
}
