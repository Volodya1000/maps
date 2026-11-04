using CableRoutePlanner.Domain;

namespace CableRoutePlanner.Infrastructure.Routing;

/// <summary>Measured segment facts; pricing remains exclusively in Domain.</summary>
internal sealed record EdgeEvaluation(
    double Length,
    double Forest,
    double Field,
    double Clearing,
    IReadOnlyList<long> Roads,
    IReadOnlyList<long> Crossings,
    IReadOnlyList<long> BridgeCandidates,
    IReadOnlyList<long> GasCandidates,
    int GasRoadCrossings,
    bool PreferGas)
{
    public double Cost(CostProfile profile) => RouteCostCalculator.Calculate(
        new SegmentTerrainFacts(
            Length,
            Forest,
            Field,
            Clearing,
            Roads.Count,
            BridgeCandidates.Count,
            Crossings.Count,
            PreferGas ? GasRoadCrossings : 0),
        profile);
}

