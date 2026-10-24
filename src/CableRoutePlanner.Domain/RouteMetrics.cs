namespace CableRoutePlanner.Domain;

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
