using CableRoutePlanner.Domain;

namespace CableRoutePlanner.Application;

public sealed class PlanningSession(
    IRoutePlanner planner,
    IPointPlacementValidator pointPlacementValidator,
    IRouteElevationAnalyzer? elevationAnalyzer = null) : IPlanningSession
{
    public const int MaximumWaypointCount = 100;

    private readonly IPointPlacementValidator _pointPlacementValidator =
        pointPlacementValidator ?? throw new ArgumentNullException(nameof(pointPlacementValidator));

    private readonly List<Waypoint> _points = [];
    private long _replacementRevision;

    public IReadOnlyList<Waypoint> Points => _points.AsReadOnly();

    public long Revision
    {
        get; private set;
    }

    public async Task<Waypoint> AddAsync(GeoCoordinate coordinate, CancellationToken cancellationToken)
    {
        long replacementRevision = _replacementRevision;
        await _pointPlacementValidator.ValidateAsync(coordinate, cancellationToken).ConfigureAwait(false);
        if (replacementRevision != _replacementRevision)
        {
            throw new PlanningException("Рабочий маршрут заменён. Изменение точки отменено.");
        }
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

        long replacementRevision = _replacementRevision;
        await _pointPlacementValidator.ValidateAsync(coordinate, cancellationToken).ConfigureAwait(false);
        if (replacementRevision != _replacementRevision)
        {
            throw new PlanningException("Рабочий маршрут заменён. Изменение точки отменено.");
        }
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

    public void Restore(IReadOnlyList<Waypoint> points)
    {
        SavedRoute.ValidatePoints(points);
        Waypoint[] snapshot = points.ToArray();
        _points.Clear();
        _points.AddRange(snapshot);
        _replacementRevision++;
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
        EnsureCurrentCalculation(revision, cancellationToken);
        if (elevationAnalyzer is null)
        {
            return result;
        }

        RouteAlternative[] enriched = new RouteAlternative[result.Count];
        for (int index = 0; index < enriched.Length; index++)
        {
            enriched[index] = await elevationAnalyzer.AnalyzeAsync(result[index], cancellationToken).ConfigureAwait(false);
            EnsureCurrentCalculation(revision, cancellationToken);
        }

        return Array.AsReadOnly(enriched);
    }

    private void EnsureCurrentCalculation(long revision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Revision != revision)
        {
            throw new PlanningException("Точки изменились во время расчёта. Постройте маршруты заново.");
        }
    }
}
