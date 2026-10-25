namespace CableRoutePlanner.Domain;

public sealed record RoutingOptions(
    double CellSizeMetres = 25,
    int MaxNodes = 100000,
    double ReserveFraction = 0.10)
{
    public void Validate()
    {
        if (!double.IsFinite(CellSizeMetres) || CellSizeMetres is < 5 or > 200
            || MaxNodes is < 100 or > 1000000
            || !double.IsFinite(ReserveFraction) || ReserveFraction is < 0 or > 1)
        {
            throw new ArgumentException("Некорректные параметры расчёта.");
        }
    }
}
