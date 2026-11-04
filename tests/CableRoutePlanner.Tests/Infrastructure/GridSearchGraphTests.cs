using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using CableRoutePlanner.Infrastructure.Routing;
using NetTopologySuite.Geometries;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class GridSearchGraphTests
{
    private static readonly LocalProjection Projection = new(TestData.P(0, 0));
    private static readonly int[] CentreNeighbours = [26, 6, 7, 8, 11, 13, 16, 17, 18];
    private static readonly int[] PortalStartOrder = [6, 9];
    private static readonly int[] PortalMiddleNeighbours = [6, 8];
    private static readonly int[] FirstPortalPath = [6, 7, 8];
    private static readonly int[] SecondPortalPath = [9, 10, 11];
    private static readonly int[] LeftSideNodes = [0, 1, 5, 6, 10, 11, 15, 16, 20, 21];

    [Theory]
    [InlineData(1000, 1000, 2, 18)]
    [InlineData(1050, 1050, 2, 18)]
    [InlineData(1150, 1150, 4, 20)]
    [InlineData(5, 5, 0, 8)]
    public void EndpointConnectionsPreserveRoundedClippedSquareAndRowOrder(double x, double y, int lower, int upper)
    {
        GridMesh mesh = Mesh(21, 21);
        Coordinate start = new(x, y);
        GridSearchGraph graph = Graph(mesh, [], start, new(2000, 2000));
        (int Id, double Cost)[] connections = graph.Neighbours(graph.StartNodeId).ToArray();
        int[] expected = Enumerable.Range(lower, upper - lower + 1)
            .SelectMany(row => Enumerable.Range(lower, upper - lower + 1).Select(column => row * 21 + column)).ToArray();

        Assert.Equal(expected, connections.Select(edge => edge.Id));
        Assert.All(connections, edge => Assert.Equal(start.Distance(mesh.Nodes[edge.Id]), edge.Cost, 8));
        Assert.Equal(start, graph.GetCoordinate(graph.StartNodeId));
        Assert.Equal(0, graph.EstimateRemainingCost(graph.DestinationNodeId));
        if (x == 1000)
        {
            Assert.Contains(connections, edge => edge.Id == 220 && edge.Cost == 0);
            Assert.True(connections[0].Cost > mesh.CellSizeMetres * 8);
        }
    }

    [Fact]
    public void GridFactsStayLazyAndNeighbourOrderDoesNotChange()
    {
        GridMesh mesh = Mesh();
        TerrainEvaluator evaluator = new([], Projection);
        GridEdgeEvaluationCache edges = new(mesh, evaluator);
        GridSearchGraph graph = new(new(0, 0), new(400, 400), mesh, CostProfile.Defaults[0], evaluator, edges, default);
        Assert.Equal(0, edges.EvaluationCount);
        _ = graph.Neighbours(graph.StartNodeId).ToArray();
        Assert.Equal(0, edges.EvaluationCount);

        (int Id, double Cost)[] neighbours = graph.Neighbours(12).ToArray();

        Assert.Equal(CentreNeighbours, neighbours.Select(edge => edge.Id));
        Assert.Equal(8, edges.EvaluationCount);
        Assert.All(neighbours, edge => Assert.True(double.IsFinite(edge.Cost) && edge.Cost >= 0));
        _ = graph.Neighbours(12).ToArray();
        Assert.Equal(8, edges.EvaluationCount);
    }

    [Fact]
    public void BlockedCandidatesAreOmittedWithoutLosingOtherConnections()
    {
        GridMesh mesh = Mesh();
        MapFeature wall = TestData.Polygon(1, TerrainKind.Building, 180, -100, 220, 500);
        GridSearchGraph graph = Graph(mesh, [wall], new(0, 0), new(400, 400));

        Assert.Equal(LeftSideNodes, graph.Neighbours(graph.StartNodeId).Select(edge => edge.Id));
        Assert.Null(new AStarGridPathfinder().FindPath(graph, default));
    }

    [Fact]
    public void FartherConnectionsSurviveWhenTheNearestFourNodesAreBlocked()
    {
        GridMesh mesh = Mesh();
        MapFeature[] buildings = new[] { (0, 0), (100, 0), (0, 100), (100, 100) }
            .Select((point, index) => TestData.Polygon(index + 1, TerrainKind.Building,
                point.Item1 - 10, point.Item2 - 10, point.Item1 + 10, point.Item2 + 10)).ToArray();
        Coordinate start = new(50, 50);
        GridSearchGraph graph = Graph(mesh, buildings, start, new(400, 400));
        (int Id, double Cost)[] connections = graph.Neighbours(graph.StartNodeId).ToArray();

        Assert.NotEmpty(connections);
        Assert.DoesNotContain(connections, edge => edge.Id is 0 or 1 or 5 or 6);
        Assert.Contains(connections, edge => edge.Id == 2);
        Assert.All(connections, edge => Assert.True(edge.Cost > 150));
        Assert.NotNull(new AStarGridPathfinder().FindPath(graph, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingGridConnectionsRemainTypedWhetherOutOfRangeOrBlocked(bool blocked)
    {
        MapFeature[] features = blocked ? [TestData.Polygon(1, TerrainKind.Marsh, -20, -20, 500, 500)] : [];
        Coordinate start = blocked ? new(0, 0) : new(10000, 10000);

        PlanningException error = Assert.Throws<PlanningException>(() => Graph(Mesh(), features, start, new(400, 400)));

        Assert.Equal(PlanningErrorReason.GridConnectionUnavailable, error.Reason);
    }

    [Theory]
    [InlineData(-0.001)]
    [InlineData(0)]
    [InlineData(0.001)]
    public void DirectFallbackHasAnInclusiveTwoCellBoundary(double offset)
    {
        GridMesh mesh = new([new(10000, 10000)], 1, 1, new(10000, 10000), 100);
        Coordinate start = new(0, 0);
        Coordinate end = new(200 + offset, 0);
        if (offset > 0)
        {
            PlanningException error = Assert.Throws<PlanningException>(() => Graph(mesh, [], start, end));
            Assert.Equal(PlanningErrorReason.GridConnectionUnavailable, error.Reason);
            return;
        }
        GridSearchGraph graph = Graph(mesh, [], start, end);
        (int Id, double Cost) connection = Assert.Single(graph.Neighbours(graph.StartNodeId));
        Assert.Equal(graph.DestinationNodeId, connection.Id);
        Assert.Equal(200 + offset, connection.Cost, 8);
        IReadOnlyList<int> path = Assert.IsAssignableFrom<IReadOnlyList<int>>(new AStarGridPathfinder().FindPath(graph, default));
        Assert.Equal(new[] { start, end }, path.Select(graph.GetCoordinate));
    }

    [Fact]
    public void AdaptiveSpacingStillConnectsArbitraryEndpoints()
    {
        GeoCoordinate origin = TestData.P(0, 0);
        GeoCoordinate end = TestData.P(20000, 10000);
        GridMesh mesh = new GridGenerator().Generate(GeoBounds.Around([origin, end]), new(25, 1000), Projection);
        Assert.True(mesh.CellSizeMetres > 200);
        Coordinate start = new(mesh.Minimum.X + mesh.CellSizeMetres * 0.4, mesh.Minimum.Y + mesh.CellSizeMetres * 0.4);
        Coordinate finish = new(mesh.Nodes[^1].X - mesh.CellSizeMetres * 0.3, mesh.Nodes[^1].Y - mesh.CellSizeMetres * 0.3);
        GridSearchGraph graph = Graph(mesh, [], start, finish);

        IReadOnlyList<int> path = Assert.IsAssignableFrom<IReadOnlyList<int>>(new AStarGridPathfinder().FindPath(graph, default));

        Assert.Equal(start, graph.GetCoordinate(path[0]));
        Assert.Equal(finish, graph.GetCoordinate(path[^1]));
        Assert.NotEmpty(graph.Neighbours(graph.StartNodeId));
    }

    [Fact]
    public void NeighbouringBridgePortalsKeepTheirNumberingGeometryAndConnectionOrder()
    {
        GridMesh mesh = Mesh(3, 2);
        Coordinate[] first = [new(90, 40), new(100, 45), new(110, 40)];
        Coordinate[] second = [new(90, 60), new(100, 65), new(110, 60)];
        mesh = mesh with { Portals = [new(first), new(second)] };
        MapFeature[] features =
        [
            TestData.Polygon(1, TerrainKind.River, 95, -1000, 105, 1000),
            TestData.Line(2, TerrainKind.BridgeCandidate, (90, 40), (100, 45), (110, 40)),
            TestData.Line(3, TerrainKind.BridgeCandidate, (90, 60), (100, 65), (110, 60))
        ];
        GridSearchGraph graph = Graph(mesh, features, new(-10, 50), new(210, 50));

        Assert.Equal(12, graph.StartNodeId);
        Assert.Equal(13, graph.DestinationNodeId);
        Assert.Equal(first.Concat(second), Enumerable.Range(6, 6).Select(graph.GetCoordinate));
        Assert.Equal(PortalStartOrder, graph.Neighbours(0).Where(edge => edge.Id >= 6).Select(edge => edge.Id));
        Assert.Equal(PortalMiddleNeighbours, graph.Neighbours(7).Select(edge => edge.Id));
        IReadOnlyList<int> path = Assert.IsAssignableFrom<IReadOnlyList<int>>(new AStarGridPathfinder().FindPath(graph, default));
        int[] visited = path.Where(id => id is >= 6 and < 12).ToArray();
        Assert.True(visited.SequenceEqual(FirstPortalPath) || visited.SequenceEqual(SecondPortalPath));
    }

    [Fact]
    public void GasPortalUsesMappedVerticesAndNeverInventsPermissionForAWaterBarrier()
    {
        Coordinate[] span = [new(90, 50), new(100, 60), new(110, 50)];
        GridMesh mesh = Mesh(3, 2) with { Portals = [new(span)] };
        MapFeature road = TestData.Line(1, TerrainKind.Road, (100, -1000), (100, 1000));
        MapFeature gas = TestData.Line(2, TerrainKind.GasPipelineCrossingCandidate, (90, 50), (100, 60), (110, 50));
        GridSearchGraph graph = Graph(mesh, [road, gas], new(-10, 50), new(210, 50));

        Assert.Equal(span, Enumerable.Range(6, 3).Select(graph.GetCoordinate));
        Assert.Equal(PortalMiddleNeighbours, graph.Neighbours(7).Select(edge => edge.Id));
        Assert.NotNull(new AStarGridPathfinder().FindPath(graph, default));

        MapFeature river = TestData.Polygon(3, TerrainKind.River, 95, -1000, 105, 1000);
        GridSearchGraph blocked = Graph(mesh, [road, gas, river], new(-10, 50), new(210, 50));
        Assert.Null(new AStarGridPathfinder().FindPath(blocked, default));
    }

    [Fact]
    public void BlockedPortalLandingCannotJumpAnObstacle()
    {
        Coordinate[] span = [new(90, 50), new(100, 60), new(110, 50)];
        GridMesh mesh = Mesh(3, 2) with { Portals = [new(span)] };
        MapFeature[] features =
        [
            TestData.Polygon(1, TerrainKind.River, 95, -1000, 105, 1000),
            TestData.Line(2, TerrainKind.BridgeCandidate, (90, 50), (100, 60), (110, 50)),
            TestData.Polygon(3, TerrainKind.Building, 80, 30, 94, 70)
        ];
        GridSearchGraph graph = Graph(mesh, features, new(-10, 50), new(210, 50));

        Assert.Empty(graph.Neighbours(6));
        Assert.Null(new AStarGridPathfinder().FindPath(graph, default));
    }

    [Theory]
    [InlineData(TerrainKind.BridgeCandidate, -30)]
    [InlineData(TerrainKind.GasPipelineCrossingCandidate, -30)]
    [InlineData(TerrainKind.BridgeCandidate, 30)]
    [InlineData(TerrainKind.GasPipelineCrossingCandidate, 30)]
    public async Task PlannerOnlyAdmitsPortalsInsideTheLoadedRegion(TerrainKind kind, double y)
    {
        MapFeature barrier = kind == TerrainKind.BridgeCandidate
            ? TestData.Polygon(1, TerrainKind.River, 140, -1000, 180, 1000)
            : TestData.Line(1, TerrainKind.Road, (160, -1000), (160, 1000));
        IRoutePlanner planner = TestData.Planner(barrier, TestData.Line(2, kind, (130, y), (190, y)));
        GeoCoordinate[] points = [TestData.P(50, 150), TestData.P(280, 150)];
        if (y < 0)
        {
            PlanningException error = await Assert.ThrowsAsync<PlanningException>(() => planner.PlanAsync(points, TestData.Options, default));
            Assert.Equal(PlanningErrorReason.NoRouteInSearchArea, error.Reason);
        }
        else
        {
            IReadOnlyList<RouteAlternative> routes = await planner.PlanAsync(points, TestData.Options, default);
            Assert.NotEmpty(routes);
            Assert.All(routes, route => Assert.All(route.Geometry, point => Assert.True(new MemoryData().Info.Contains(point))));
            Assert.Contains(routes, route => kind == TerrainKind.BridgeCandidate
                ? route.Metrics.PotentialBridgeCrossings == 1 : route.GasPipelineCandidateIds!.Contains(2));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ProfilePricesAndAdmissibleHeuristicsAgreeWithDijkstra(int profileIndex)
    {
        GridMesh mesh = Mesh(7, 5);
        MapFeature[] features =
        [
            TestData.Polygon(1, TerrainKind.Forest, 100, 50, 500, 250),
            TestData.Polygon(2, TerrainKind.Clearing, -20, 280, 620, 420)
        ];
        TerrainEvaluator evaluator = new(features, Projection);
        GridEdgeEvaluationCache cache = new(mesh, evaluator);
        CostProfile profile = CostProfile.Defaults[profileIndex];
        GridSearchGraph graph = new(new(0, 150), new(600, 150), mesh, profile, evaluator, cache, default);
        AStarGridPathfinder search = new();
        IReadOnlyList<int> path = Assert.IsAssignableFrom<IReadOnlyList<int>>(search.FindPath(graph, default));
        IReadOnlyList<int> reference = Assert.IsAssignableFrom<IReadOnlyList<int>>(search.FindPath(new ZeroHeuristicGraph(graph), default));
        double cost = PathCost(graph, path);

        Assert.Equal(PathCost(graph, reference), cost, 6);
        Assert.True(graph.EstimateRemainingCost(graph.StartNodeId) <= cost);
        Assert.Equal(graph.StartNodeId, path[0]);
        Assert.Equal(graph.DestinationNodeId, path[^1]);
        Assert.All(path.SelectMany(graph.Neighbours), edge => Assert.True(double.IsFinite(edge.Cost) && edge.Cost >= 0));
        if (profileIndex == 2)
        { Assert.Contains(path.Select(graph.GetCoordinate), point => point.Y >= 280); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EachProfilePricesTheSameCachedRoadAndBridgeFacts(int profileIndex)
    {
        GridMesh mesh = Mesh();
        TerrainEvaluator evaluator = new([
            TestData.Line(1, TerrainKind.Road, (250, -100), (250, 500)),
            TestData.Line(2, TerrainKind.BridgeCandidate, (230, 200), (270, 200))], Projection);
        GridEdgeEvaluationCache cache = new(mesh, evaluator);
        foreach (CostProfile profile in CostProfile.Defaults.Take(profileIndex + 1))
        {
            GridSearchGraph graph = new(new(0, 200), new(400, 200), mesh, profile, evaluator, cache, default);
            double price = Assert.Single(graph.Neighbours(12), edge => edge.Id == 13).Cost;
            Assert.Equal(100 + 2 * profile.RoadCrossingMetres, price, 6);
            Assert.Equal(8, cache.EvaluationCount);
        }
        EdgeEvaluation facts = Assert.IsType<EdgeEvaluation>(cache.Get(12, 13));
        Assert.Equal(1, Assert.Single(facts.Roads));
        Assert.Equal(2, Assert.Single(facts.BridgeCandidates));
        Assert.Equal(8, cache.EvaluationCount);
    }

    [Fact]
    public void EndpointPreparationHonoursCancellation()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        GridMesh mesh = Mesh();
        TerrainEvaluator evaluator = new([], Projection);
        Assert.Throws<OperationCanceledException>(() => new GridSearchGraph(new(0, 0), new(400, 400),
            mesh, CostProfile.Defaults[0], evaluator, new(mesh, evaluator), cancellation.Token));
    }

    private static double PathCost(GridSearchGraph graph, IReadOnlyList<int> path) =>
        path.Zip(path.Skip(1), (from, to) => graph.Neighbours(from).Single(edge => edge.Id == to).Cost).Sum();

    private sealed class ZeroHeuristicGraph(IRoutingGraph graph) : IRoutingGraph
    {
        public int NodeCount => graph.NodeCount;
        public int StartNodeId => graph.StartNodeId;
        public int DestinationNodeId => graph.DestinationNodeId;
        public IEnumerable<(int Id, double Cost)> Neighbours(int nodeId) => graph.Neighbours(nodeId);
        public double EstimateRemainingCost(int nodeId) => 0;
    }

    private static GridSearchGraph Graph(GridMesh mesh, IReadOnlyList<MapFeature> features, Coordinate start, Coordinate end)
    {
        TerrainEvaluator evaluator = new(features, Projection);
        return new(start, end, mesh, CostProfile.Defaults[0], evaluator, new(mesh, evaluator), default);
    }

    private static GridMesh Mesh(int columns = 5, int rows = 5) => new(
        Enumerable.Range(0, rows).SelectMany(row => Enumerable.Range(0, columns)
            .Select(column => new Coordinate(column * 100, row * 100))).ToArray(), columns, rows, new(0, 0), 100);
}
