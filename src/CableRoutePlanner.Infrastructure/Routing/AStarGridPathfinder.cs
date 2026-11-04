namespace CableRoutePlanner.Infrastructure.Routing;

/// <summary>Substitutable shortest-path algorithm over a typed weighted graph.</summary>
public interface IGridPathfinder
{
    /// <summary>Finds an ordered node path, or null when the finite graph has no permitted path.</summary>
    /// <param name="graph">Stable node IDs, permitted costs and an admissible lower-bound heuristic.</param>
    /// <param name="cancellationToken">Cancels search without publishing a partial path.</param>
    /// <returns>Start-to-destination IDs, including both endpoints, or null.</returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    IReadOnlyList<int>? FindPath(IRoutingGraph graph, CancellationToken cancellationToken);
}

/// <summary>Array-backed A*; graph construction, geometry and engineering costs belong to the graph.</summary>
/// <remarks>
/// Priorities are accumulated cost plus an admissible remaining-cost estimate.
/// Stale queue entries are skipped; improved nodes can be enqueued again. Stable
/// neighbour order preserves the existing tie behavior. Memory is O(NodeCount)
/// plus queued improvements. A zero heuristic is the Dijkstra reference case.
/// </remarks>
public sealed class AStarGridPathfinder : IGridPathfinder
{
    public IReadOnlyList<int>? FindPath(IRoutingGraph graph, CancellationToken cancellationToken)
    {
        PriorityQueue<(int Id, double Cost), double> queue = new();
        double[] distances = Enumerable.Repeat(double.PositiveInfinity, graph.NodeCount).ToArray();
        int[] parents = Enumerable.Repeat(-1, graph.NodeCount).ToArray();
        distances[graph.StartNodeId] = 0;
        queue.Enqueue((graph.StartNodeId, 0), 0);
        while (queue.TryDequeue(out (int Id, double Cost) current, out _))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.Cost > distances[current.Id])
            { continue; }
            if (current.Id == graph.DestinationNodeId)
            { return ReconstructPath(graph.DestinationNodeId, parents); }

            foreach ((int next, double edgeCost) in graph.Neighbours(current.Id))
            {
                double cost = current.Cost + edgeCost;
                if (cost >= distances[next])
                { continue; }
                distances[next] = cost;
                parents[next] = current.Id;
                queue.Enqueue((next, cost), cost + graph.EstimateRemainingCost(next));
            }
        }
        return null;
    }

    private static List<int> ReconstructPath(int destination, int[] parents)
    {
        List<int> path = [];
        for (int id = destination; id != -1; id = parents[id])
        { path.Add(id); }
        path.Reverse();
        return path;
    }

}
