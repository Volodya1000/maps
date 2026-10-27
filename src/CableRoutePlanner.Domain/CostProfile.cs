namespace CableRoutePlanner.Domain;

public sealed record CostProfile(
    string Name,
    double Forest,
    double Field,
    double RoadCrossingMetres,
    double Clearing)
{
    public const double MinimumClearingMultiplier = 0;
    public const double MaximumClearingMultiplier = 1;

    /// <summary>A lower bound on cost per physical metre, used by admissible search heuristics.</summary>
    public double MinimumCostPerMetre => Math.Min(1, Clearing);

    public static IReadOnlyList<CostProfile> Defaults { get; } = Array.AsReadOnly(new[]
    {
        // The shortest alternative minimizes physical cable length. Terrain preferences
        // belong to the other profiles; crossing penalties must not hide a bridge detour.
        new CostProfile("Короче", 0, 0, 0, 1),
        new CostProfile("Баланс", 3, 0.8, 150, 0.65),
        new CostProfile("Проще прокладка", 8, 2, 400, 0.5)
    });

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("Название профиля должно быть непустым.", nameof(Name));
        }

        ValidateNonNegative(Forest, nameof(Forest), "Стоимость прохождения леса");
        ValidateNonNegative(Field, nameof(Field), "Стоимость прохождения поля");
        ValidateNonNegative(RoadCrossingMetres, nameof(RoadCrossingMetres), "Штраф за пересечение дороги");
        if (!double.IsFinite(Clearing) || Clearing is <= MinimumClearingMultiplier or > MaximumClearingMultiplier)
        {
            throw new ArgumentOutOfRangeException(nameof(Clearing), Clearing,
                $"Множитель стоимости просеки должен быть конечным и находиться в диапазоне ({MinimumClearingMultiplier}, {MaximumClearingMultiplier}].");
        }
    }

    private static void ValidateNonNegative(double value, string parameterName, string label)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value,
                $"{label} должна быть конечной и неотрицательной.");
        }
    }
}
