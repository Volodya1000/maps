using CableRoutePlanner.Domain;

namespace CableRoutePlanner.Application;

/// <summary>A calculation snapshot, independent of the current map and settings.</summary>
public sealed record SavedRoute(
    Guid Id,
    string? Name,
    DateTimeOffset AddedUtc,
    IReadOnlyList<Waypoint> Points,
    IReadOnlyList<RouteAlternative> Alternatives,
    Guid SelectedAlternativeId,
    RoutingOptions Options)
{
    public void Validate()
    {
        if (Id == Guid.Empty || AddedUtc == default || AddedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException($"SavedRoute.Id={Id}, AddedUtc={AddedUtc:O}: требуется ID и дата UTC.");
        }
        ValidatePoints(Points);
        ArgumentNullException.ThrowIfNull(Options);
        Options.Validate();
        ArgumentNullException.ThrowIfNull(Alternatives);
        if (Alternatives.Count == 0 || Alternatives.Any(route => route is null || route.Id == Guid.Empty)
            || Alternatives.Select(route => route.Id).Distinct().Count() != Alternatives.Count
            || !Alternatives.Any(route => route.Id == SelectedAlternativeId))
        {
            throw new ArgumentException($"SavedRoute.Alternatives.Count={Alternatives.Count}, SelectedAlternativeId={SelectedAlternativeId}: требуются уникальные варианты и выбранный ID из этого списка.");
        }
        foreach (RouteAlternative route in Alternatives)
        {
            ValidateAlternative(route);
        }
    }

    public static void ValidatePoints(IReadOnlyList<Waypoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count is < 2 or > PlanningSession.MaximumWaypointCount
            || points.Any(point => point is null || point.Id == Guid.Empty)
            || points.Select(point => point.Id).Distinct().Count() != points.Count)
        {
            throw new ArgumentException($"SavedRoute.Points.Count={points.Count}: требуется от 2 до {PlanningSession.MaximumWaypointCount} точек с уникальными ID.", nameof(points));
        }
    }

    private static void ValidateAlternative(RouteAlternative route)
    {
        ArgumentNullException.ThrowIfNull(route.Geometry);
        ArgumentNullException.ThrowIfNull(route.Metrics);
        if (route.Geometry.Count < 2 || string.IsNullOrWhiteSpace(route.Name))
        {
            throw new ArgumentException($"RouteAlternative.Id={route.Id}, Geometry.Count={route.Geometry.Count}, Name={route.Name}: требуется геометрия из минимум двух точек и имя.");
        }
        route.ValidateElevationProfile();
        RouteMetrics metrics = route.Metrics;
        double[] distances = [metrics.LengthMetres, metrics.ForestMetres, metrics.FieldMetres, metrics.WeightedCost];
        int[] counts = [metrics.RoadCrossings, metrics.RailwayCrossings, metrics.ApprovedWaterCrossings,
            metrics.PotentialBridgeCrossings, metrics.PotentialGasPipelineCrossings];
        if (distances.Any(value => !double.IsFinite(value) || value < 0)
            || counts.Any(value => value < 0)
            || metrics.AscentMetres is { } ascent && (!double.IsFinite(ascent) || ascent < 0))
        {
            throw new ArgumentException($"RouteAlternative.Id={route.Id}, Metrics={metrics}: расстояния и счётчики должны быть конечными и неотрицательными.");
        }
        if (route.RoadContacts?.Any(contact => contact is null || !Enum.IsDefined(typeof(RoadContactKind), contact.Kind)
                || contact.Geometry is null || contact.Geometry.Count < 1) == true
            || route.RoadCrossingEvents?.Any(crossing => crossing is null || !Enum.IsDefined(typeof(RoadCrossingKind), crossing.Kind)) == true
            || route.UnderpassPassages?.Any(passage => passage is null) == true)
        {
            throw new ArgumentException($"RouteAlternative.Id={route.Id}: недопустимые сведения о переходах.");
        }
    }
}

public interface ISavedRouteStore
{
    IReadOnlyList<SavedRoute> List();
    SavedRoute Read(Guid id);
    void Add(SavedRoute route);
    void Delete(Guid id);
}
