using System.Collections.ObjectModel;
using CableRoutePlanner.Domain;

namespace CableRoutePlanner.ViewModels;

internal sealed class WaypointListProjection
{
    public ObservableCollection<WaypointRow> Points { get; } = [];

    public int IndexOf(Guid pointId)
    {
        for (var index = 0; index < Points.Count; index++)
        {
            if (Points[index].Id == pointId)
            {
                return index;
            }
        }

        return -1;
    }

    public WaypointRow? Refresh(IReadOnlyList<Waypoint> waypoints, Guid? selectedId)
    {
        selectedId = waypoints.FirstOrDefault(point => point.Id == selectedId)?.Id
            ?? (waypoints.Count > 0 ? waypoints[^1].Id : null);
        Points.Clear();
        for (var index = 0; index < waypoints.Count; index++)
        {
            var waypoint = waypoints[index];
            Points.Add(new WaypointRow(waypoint.Id, index + 1, waypoint.Coordinate, waypoint.Id == selectedId));
        }

        return Points.FirstOrDefault(point => point.Id == selectedId) ?? Points.LastOrDefault();
    }
}
