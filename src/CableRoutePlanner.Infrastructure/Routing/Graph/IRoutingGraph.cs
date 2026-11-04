namespace CableRoutePlanner.Infrastructure.Routing;

/// <summary>A finite weighted search graph with stable, dense node identifiers.</summary>
/// <remarks>
/// Nodes occupy [0, NodeCount). Neighbours contain only permitted edges with finite,
/// nonnegative cost. The graph and its neighbour order remain stable during a search.
/// Geometry, engineering policy, units and storage are outside the A* contract.
/// </remarks>
public interface IRoutingGraph
{
    int NodeCount { get; }
    int StartNodeId { get; }
    int DestinationNodeId { get; }

    /// <summary>Enumerates permitted transitions in deterministic order.</summary>
    IEnumerable<(int Id, double Cost)> Neighbours(int nodeId);

    /// <summary>Returns an admissible lower bound on remaining cost; zero gives Dijkstra's search.</summary>
    /// <remarks>Must never overestimate the least possible remaining cost and must be zero at the destination.</remarks>
    double EstimateRemainingCost(int nodeId);
}
