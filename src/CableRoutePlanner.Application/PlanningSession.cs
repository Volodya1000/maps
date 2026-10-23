using CableRoutePlanner.Domain;
namespace CableRoutePlanner.Application;

public sealed class PlanningSession(IRoutePlanner planner) : IPlanningSession
{
    private readonly List<Waypoint> _points = [];
    public IReadOnlyList<Waypoint> Points => _points.AsReadOnly();
    public long Revision { get; private set; }
    public Waypoint Add(GeoCoordinate coordinate)
    {
        if (_points.Count >= 100) throw new PlanningException("Допускается не более 100 точек.");
        var point = new Waypoint(Guid.NewGuid(), coordinate);
        _points.Add(point); Revision++; return point;
    }
    public void Move(Guid id, GeoCoordinate coordinate)
    {
        var index = _points.FindIndex(p => p.Id == id);
        if (index < 0) throw new PlanningException("Точка не найдена.");
        _points[index] = _points[index] with { Coordinate = coordinate }; Revision++;
    }
    public void Remove(Guid id) { if (_points.RemoveAll(p => p.Id == id) > 0) Revision++; }
    public void Reorder(int oldIndex, int newIndex)
    {
        if (oldIndex < 0 || newIndex < 0 || oldIndex >= _points.Count || newIndex >= _points.Count)
            throw new PlanningException("Некорректный порядок точек.");
        var point = _points[oldIndex]; _points.RemoveAt(oldIndex); _points.Insert(newIndex, point); Revision++;
    }
    public void Clear() { _points.Clear(); Revision++; }
    public async Task<IReadOnlyList<RouteAlternative>> BuildAsync(RoutingOptions options, CancellationToken cancellationToken)
    {
        if (_points.Count < 2) throw new PlanningException("Установите минимум две точки.");
        options.Validate();
        var revision = Revision;
        var coordinates = _points.Select(p => p.Coordinate).ToArray();
        var result = await planner.PlanAsync(coordinates, options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (Revision != revision) throw new PlanningException("Точки изменились во время расчёта. Постройте маршруты заново.");
        return result;
    }
}
