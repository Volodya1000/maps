using CableRoutePlanner.Infrastructure.Routing;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class AStarGraphTests
{
    private static readonly int[] CheapestPath = [0, 2, 1, 3];
    private static readonly int[] SingleNodePath = [0];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WeightedGraphFindsAndReconstructsTheKnownCheapestPath(bool useHeuristic)
    {
        ExampleGraph graph = WeightedGraph(useHeuristic);

        IReadOnlyList<int>? path = new AStarGridPathfinder().FindPath(graph, default);

        Assert.NotNull(path);
        Assert.Equal(CheapestPath, path);
        Assert.Equal(4, path.Zip(path.Skip(1), (from, to) => graph.Neighbours(from).Single(edge => edge.Id == to).Cost).Sum());
    }

    [Fact]
    public void AdmissibleHeuristicPreservesTheZeroHeuristicOptimum()
    {
        AStarGridPathfinder search = new();

        IReadOnlyList<int>? dijkstra = search.FindPath(WeightedGraph(useHeuristic: false), default);
        IReadOnlyList<int>? heuristic = search.FindPath(WeightedGraph(useHeuristic: true), default);

        Assert.Equal(CheapestPath, dijkstra);
        Assert.Equal(dijkstra, heuristic);
    }

    [Fact]
    public void DisconnectedGraphReturnsNoPath()
    {
        ExampleGraph graph = new([[(1, 1)], [], [], []], [0, 0, 0, 0]);

        Assert.Null(new AStarGridPathfinder().FindPath(graph, default));
    }

    [Fact]
    public void CoincidentStartAndDestinationReturnsOneNode()
    {
        ExampleGraph graph = new([[]], [0], destination: 0);

        Assert.Equal(SingleNodePath, new AStarGridPathfinder().FindPath(graph, default));
    }

    [Fact]
    public void CancelledSearchDoesNotReturnAPartialPath()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => new AStarGridPathfinder().FindPath(
            WeightedGraph(useHeuristic: true), cancellation.Token));
    }

    private static ExampleGraph WeightedGraph(bool useHeuristic) => new(
        [[(1, 5), (2, 2)], [(3, 1)], [(1, 1), (3, 8)], []], useHeuristic ? [4, 1, 2, 0] : [0, 0, 0, 0]);

    private sealed class ExampleGraph(IReadOnlyList<(int Id, double Cost)>[] neighbours, double[] estimates,
        int destination = 3) : IRoutingGraph
    {
        public int NodeCount => neighbours.Length;
        public int StartNodeId => 0;
        public int DestinationNodeId => destination;
        public IEnumerable<(int Id, double Cost)> Neighbours(int nodeId) => neighbours[nodeId];
        public double EstimateRemainingCost(int nodeId) => estimates[nodeId];
    }
}
