using CableRoutePlanner.Domain;

namespace CableRoutePlanner.Application;

public sealed class PlanningSession(
    IRoutePlanner planner,
    IPointPlacementValidator pointPlacementValidator) : IPlanningSession
{
    public const int MaximumWaypointCount = 100;

    private readonly IPointPlacementValidator _pointPlacementValidator =
        pointPlacementValidator ?? throw new ArgumentNullException(nameof(pointPlacementValidator));

    private readonly List<Waypoint> _points = [];

    public IReadOnlyList<Waypoint> Points => _points.AsReadOnly();

    public long Revision
    {
        get; private set;
    }

    public async Task<Waypoint> AddAsync(GeoCoordinate coordinate, CancellationToken cancellationToken)
    {
        await _pointPlacementValidator.ValidateAsync(coordinate, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (_points.Count >= MaximumWaypointCount)
        {
            throw new PlanningException($"Допускается не более {MaximumWaypointCount} точек.");
        }

        var point = new Waypoint(Guid.NewGuid(), coordinate);
        _points.Add(point);
        Revision++;
        return point;
    }

    public async Task MoveAsync(Guid id, GeoCoordinate coordinate, CancellationToken cancellationToken)
    {
        int index = _points.FindIndex(p => p.Id == id);
        if (index < 0)
        {
            throw new PlanningException("Точка не найдена.");
        }

        await _pointPlacementValidator.ValidateAsync(coordinate, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _points[index] = _points[index] with
        {
            Coordinate = coordinate
        };
        Revision++;
    }

    public void Remove(Guid id)
    {
        if (_points.RemoveAll(p => p.Id == id) > 0)
        {
            Revision++;
        }
    }

    public void Reorder(int oldIndex, int newIndex)
    {
        if (oldIndex < 0 || newIndex < 0 || oldIndex >= _points.Count || newIndex >= _points.Count)
        {
            throw new PlanningException("Некорректный порядок точек.");
        }

        Waypoint point = _points[oldIndex];
        _points.RemoveAt(oldIndex);
        _points.Insert(newIndex, point);
        Revision++;
    }

    public void Clear()
    {
        _points.Clear();
        Revision++;
    }

    public async Task<IReadOnlyList<RouteAlternative>> BuildAsync(
        RoutingOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_points.Count < 2)
        {
            throw new PlanningException("Установите минимум две точки.");
        }

        options.Validate();
        long revision = Revision;
        GeoCoordinate[] coordinates = _points.Select(p => p.Coordinate).ToArray();
        IReadOnlyList<RouteAlternative> result = await planner.PlanAsync(coordinates, options, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (Revision != revision)
        {
            throw new PlanningException("Точки изменились во время расчёта. Постройте маршруты заново.");
        }

        return result;
    }
}
