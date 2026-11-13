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

    /// <summary>Classifies every supported object explicitly; an unknown kind cannot silently acquire permission.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The source supplies a terrain kind without a declared policy.</exception>
    public static TerrainTraversalRule For(TerrainKind terrain) => terrain switch
    {
        TerrainKind.Marsh or TerrainKind.Lake or TerrainKind.River or TerrainKind.Building => TerrainTraversalRule.Blocked,
        TerrainKind.Forest or TerrainKind.Field or TerrainKind.Clearing => TerrainTraversalRule.Costed,
        TerrainKind.Road => TerrainTraversalRule.ConditionalTransition,
        TerrainKind.BridgeCandidate or TerrainKind.GasPipelineCrossingCandidate => TerrainTraversalRule.ConditionalTransition,
        TerrainKind.ApprovedCrossing => TerrainTraversalRule.ApprovedTransition,
        TerrainKind.Settlement or TerrainKind.OverheadGasPipeline or TerrainKind.PedestrianUnderpassCandidate => TerrainTraversalRule.Neutral,
        _ => throw new ArgumentOutOfRangeException(nameof(terrain), terrain, "Для типа местности не определено правило проходимости.")
    };
}
