namespace CableRoutePlanner.Domain;

public sealed record CostProfile(
    string Name,
    double Forest,
    double Field,
    double RoadCrossingMetres,
    double Clearing)
{
    // Engineering heuristic penalties, expressed as equivalent metres rather than tariffs.
    // The profile assumptions and units are documented in docs/routing-algorithm.md.
    public static IReadOnlyList<CostProfile> Defaults
    {
        get;
    } = Array.AsReadOnly(
        new[] {
            // The shortest alternative minimizes physical cable length. Terrain preferences
            // belong to the other profiles; crossing penalties must not hide a bridge detour.
            new CostProfile("Короче", 0, 0, 0, 1),
            new CostProfile("Баланс", 3, 0.8, 150, 0.65),
            new CostProfile("Проще прокладка", 8, 2, 400, 0.5)
        });

    public void Validate()
    {
        if (!double.IsFinite(Forest)
            || Forest < 0
            || !double.IsFinite(Field)
            || Field < 0
            || !double.IsFinite(RoadCrossingMetres)
            || RoadCrossingMetres < 0
            || !double.IsFinite(Clearing)
            || Clearing <= 0
            || Clearing > 1)
        {
            throw new ArgumentException(
                "Стоимость должна быть конечной и неотрицательной; множитель просеки — (0, 1].");
        }
    }
}

public sealed record RouteMetrics(
    double LengthMetres,
    double ForestMetres,
    double FieldMetres,
    int RoadCrossings,
    int ApprovedWaterCrossings,
    int PotentialBridgeCrossings,
    double WeightedCost,
    double? AscentMetres)
{
    public double CableLengthMetres(double reserveFraction)
    {
        if (!double.IsFinite(reserveFraction) || reserveFraction is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(reserveFraction));
        }

        return LengthMetres * (1 + reserveFraction);
    }
}

public sealed record RouteAlternative(
    Guid Id,
    string Name,
    IReadOnlyList<GeoCoordinate> Geometry,
    RouteMetrics Metrics);

public sealed record RoutingOptions(
    double CellSizeMetres = 25,
    int MaxNodes = 100000,
    double ReserveFraction = 0.10)
{
    public void Validate()
    {
        if (!double.IsFinite(CellSizeMetres)
            || CellSizeMetres is < 5 or > 200
            || MaxNodes is < 100 or > 1000000
            || !double.IsFinite(ReserveFraction)
            || ReserveFraction is < 0 or > 1)
        {
            throw new ArgumentException("Некорректные параметры расчёта.");
        }
    }
}
