namespace CableRoutePlanner.Domain;

public sealed record CostProfile(
    string Name,
    double Forest,
    double Field,
    double RoadCrossingMetres,
    double Clearing)
{
    public static IReadOnlyList<CostProfile> Defaults
    {
        get;
    } = Array.AsReadOnly(new[]
    {
        new CostProfile("Короче", 0.5, 0.2, 30, 0.85),
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
