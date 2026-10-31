namespace CableRoutePlanner.Application;

public sealed record RoutingOptions(
    double CellSizeMetres = 25,
    int MaxNodes = 100000,
    double ReserveFraction = 0.10,
    bool PreferOverheadGasCrossings = false,
    double MaxRouteLengthMetres = 1_000_000)
{
    public const double MinCellSizeMetres = 5;
    public const double MaxCellSizeMetres = 200;
    public const int MinNodes = 100;
    public const int MaxNodesLimit = 1000000;
    public const double MinReserveFraction = 0;
    public const double MaxReserveFraction = 1;
    public const double MinRouteLengthMetres = 1;
    public const double MaxRouteLengthLimitMetres = 10_000_000;

    public void Validate()
    {
        if (!double.IsFinite(CellSizeMetres) || CellSizeMetres is < MinCellSizeMetres or > MaxCellSizeMetres)
        {
            throw new ArgumentOutOfRangeException(nameof(CellSizeMetres), CellSizeMetres,
                $"Размер ячейки должен быть конечным и находиться в диапазоне [{MinCellSizeMetres}, {MaxCellSizeMetres}] м.");
        }

        if (MaxNodes is < MinNodes or > MaxNodesLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxNodes), MaxNodes,
                $"Максимальное число узлов должно находиться в диапазоне [{MinNodes}, {MaxNodesLimit}].");
        }

        if (!double.IsFinite(ReserveFraction) || ReserveFraction is < MinReserveFraction or > MaxReserveFraction)
        {
            throw new ArgumentOutOfRangeException(nameof(ReserveFraction), ReserveFraction,
                $"Запас кабеля должен находиться в диапазоне [{MinReserveFraction}, {MaxReserveFraction}].");
        }

        if (!double.IsFinite(MaxRouteLengthMetres)
            || MaxRouteLengthMetres is < MinRouteLengthMetres or > MaxRouteLengthLimitMetres)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRouteLengthMetres), MaxRouteLengthMetres,
                $"Максимальная длина маршрута должна находиться в диапазоне "
                + $"[{MinRouteLengthMetres}, {MaxRouteLengthLimitMetres}] м.");
        }
    }
}
