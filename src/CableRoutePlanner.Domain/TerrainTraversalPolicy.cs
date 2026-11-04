namespace CableRoutePlanner.Domain;

public enum TerrainTraversalRule
{
    Blocked,
    Costed,
    CrossingPenalty,
    ConditionalTransition,
    ApprovedTransition,
    Neutral
}

/// <summary>Subject rules for cable placement; geometry and intersection checks belong to Infrastructure.</summary>
public static class TerrainTraversalPolicy
{
    /// <summary>Candidate evidence permits geometric routing, not engineering approval.</summary>
    public static bool AllowsRoadCrossing(bool hasBridgeCandidate, bool hasGasCandidate) =>
        hasBridgeCandidate || hasGasCandidate;

    public static TerrainTraversalRule For(TerrainKind terrain) => terrain switch
    {
        TerrainKind.Marsh or TerrainKind.Lake or TerrainKind.River or TerrainKind.Building => TerrainTraversalRule.Blocked,
        TerrainKind.Forest or TerrainKind.Field or TerrainKind.Clearing => TerrainTraversalRule.Costed,
        TerrainKind.Road => TerrainTraversalRule.ConditionalTransition,
        TerrainKind.BridgeCandidate or TerrainKind.GasPipelineCrossingCandidate => TerrainTraversalRule.ConditionalTransition,
        TerrainKind.ApprovedCrossing => TerrainTraversalRule.ApprovedTransition,
        _ => TerrainTraversalRule.Neutral
    };
}
