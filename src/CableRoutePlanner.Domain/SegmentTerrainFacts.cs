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
        if (!double.IsFinite(LengthMetres) || LengthMetres < 0
            || !double.IsFinite(ForestMetres) || ForestMetres < 0
            || !double.IsFinite(FieldMetres) || FieldMetres < 0
            || !double.IsFinite(ClearingMetres) || ClearingMetres < 0
            || RoadCrossings < 0 || BridgeCandidates < 0 || ApprovedCrossings < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(LengthMetres), "Некорректные характеристики участка.");
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
