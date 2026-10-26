namespace CableRoutePlanner.Domain;

/// <summary>Measured terrain contributions for one geometrically valid route segment.</summary>
public sealed record SegmentTerrainFacts(
    double LengthMetres,
    double ForestMetres,
    double FieldMetres,
    double ClearingMetres,
    int RoadCrossings,
    int BridgeCandidates,
    int ApprovedCrossings)
{
    public void Validate()
    {
        ValidateDistance(LengthMetres, nameof(LengthMetres), "Длина участка");
        ValidateDistance(ForestMetres, nameof(ForestMetres), "Длина участка в лесу");
        ValidateDistance(FieldMetres, nameof(FieldMetres), "Длина участка в поле");
        ValidateDistance(ClearingMetres, nameof(ClearingMetres), "Длина участка по просеке");
        ValidateCount(RoadCrossings, nameof(RoadCrossings), "Количество пересечений дорог");
        ValidateCount(BridgeCandidates, nameof(BridgeCandidates), "Количество потенциальных мостовых переходов");
        ValidateCount(ApprovedCrossings, nameof(ApprovedCrossings), "Количество одобренных переходов");
    }

    private static void ValidateDistance(double value, string parameterName, string label)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value,
                $"{label} должна быть конечной и неотрицательной.");
        }
    }

    private static void ValidateCount(int value, string parameterName, string label)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value,
                $"{label} не может быть отрицательным.");
        }
    }
}

public static class RouteCostCalculator
{
    public static double Calculate(SegmentTerrainFacts facts, CostProfile profile)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(profile);
        facts.Validate();
        profile.Validate();
        return facts.LengthMetres
            - facts.ClearingMetres * (1 - profile.Clearing)
            + facts.ForestMetres * profile.Forest
            + facts.FieldMetres * profile.Field
            + (facts.RoadCrossings + facts.BridgeCandidates) * profile.RoadCrossingMetres;
    }
}

public static class AlternativeRoutePolicy
{
    public const double MaximumCorridorOverlap = 0.90;
}
