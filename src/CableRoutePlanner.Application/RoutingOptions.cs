namespace CableRoutePlanner.Application;

public sealed record RoutingOptions(
    double CellSizeMetres = 25,
    int MaxNodes = 100000,
    double ReserveFraction = 0.10)
{
    public void Validate()
    {
        if (!double.IsFinite(CellSizeMetres) || CellSizeMetres is < 5 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(CellSizeMetres), CellSizeMetres,
                "Размер ячейки должен быть конечным и находиться в диапазоне [5, 200] м.");
        }

        if (MaxNodes is < 100 or > 1000000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxNodes), MaxNodes,
                "Максимальное число узлов должно находиться в диапазоне [100, 1000000].");
        }

        if (!double.IsFinite(ReserveFraction) || ReserveFraction is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ReserveFraction), ReserveFraction,
                "Запас кабеля должен находиться в диапазоне [0, 1].");
        }
    }
}
