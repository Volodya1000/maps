namespace CableRoutePlanner.Domain;

public sealed record CostProfile(
    string Name,
    double Forest,
    double Field,
    double RoadCrossingMetres,
    double Clearing)
{
    /// <summary>A lower bound on cost per physical metre, used by admissible search heuristics.</summary>
    public double MinimumCostPerMetre => Math.Min(1, Clearing);

    public static IReadOnlyList<CostProfile> Defaults
    {
        get;
    } = Array.AsReadOnly(new[]
    {
        // The shortest alternative minimizes physical cable length. Terrain preferences
        // belong to the other profiles; crossing penalties must not hide a bridge detour.
        new CostProfile("Короче", 0, 0, 0, 1),
        new CostProfile("Баланс", 3, 0.8, 150, 0.65),
        new CostProfile("Проще прокладка", 8, 2, 400, 0.5)
    });

    public void Validate()
    {
        if (!double.IsFinite(Forest) || Forest < 0
            || !double.IsFinite(Field) || Field < 0
            || !double.IsFinite(RoadCrossingMetres) || RoadCrossingMetres < 0
            || !double.IsFinite(Clearing) || Clearing <= 0 || Clearing > 1)
        {
            throw new ArgumentException(
                "Стоимость должна быть конечной и неотрицательной; множитель просеки — (0, 1].");
        }
    }
}
