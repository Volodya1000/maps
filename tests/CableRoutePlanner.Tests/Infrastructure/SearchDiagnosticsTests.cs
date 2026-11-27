using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Routing;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class SearchDiagnosticsTests
{
    [Fact]
    public async Task CancellationKeepsItsOwnOutcomeAndTheNextCalculationStartsFresh()
    {
        using CancellationTokenSource cancellation = new();
        RoutingTestLogger logger = new();
        GridRoutePlanner planner = new(new MemoryData(), new GridGenerator(),
            new CancellingPathfinder(cancellation), logger);
        GeoCoordinate[] points = [TestData.P(50, 150), TestData.P(260, 150)];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            planner.PlanAsync(points, TestData.Options, cancellation.Token));

        RoutingLogEntry cancelled = Assert.Single(logger.Events,
            entry => entry.Event.Id == 7 && Equals(entry.Fields["Cancelled"], true));
        Assert.Null(cancelled.Fields["Reason"]);
        Assert.DoesNotContain(logger.Events, entry => entry.Event.Id is 2 or 3 or 4);
        Guid cancelledId = Assert.IsType<Guid>(cancelled.Fields["CalculationId"]);
        logger.Events.Clear();

        IReadOnlyList<RouteAlternative> routes = await planner.PlanAsync(points, TestData.Options, default);

        Assert.NotEmpty(routes);
        Assert.All(logger.Events.Where(entry => entry.Event.Id >= 5), entry =>
            Assert.NotEqual(cancelledId, entry.Fields["CalculationId"]));
        Assert.All(logger.Events.Where(entry => entry.Event.Id == 7), entry =>
            Assert.False(Assert.IsType<bool>(entry.Fields["Cancelled"])));
    }

    [Fact]
    public async Task SuccessfulSearchReportsActualCoarsenedMeshWithoutChangingRoutes()
    {
        RoutingTestLogger logger = new();
        GridRoutePlanner planner = new(new MemoryData(), new GridGenerator(), new AStarGridPathfinder(), logger);
        GeoCoordinate[] points = [TestData.P(50, 150), TestData.P(260, 150)];
        RoutingOptions options = new(5, 1000);

        IReadOnlyList<RouteAlternative> routes = await planner.PlanAsync(points, options, default);
        IReadOnlyList<RouteAlternative> reference = await TestData.Planner().PlanAsync(points, options, default);

        Assert.Equal(reference.Select(route => route.Geometry), routes.Select(route => route.Geometry));
        Assert.Equal(reference.Select(route => route.Metrics), routes.Select(route => route.Metrics));
        RoutingLogEntry mesh = Assert.Single(logger.Events, entry => entry.Event.Id == 5);
        Assert.Equal(5d, mesh.Fields["RequestedCellSizeMetres"]);
        Assert.True(Assert.IsType<double>(mesh.Fields["CellSizeMetres"]) > 5);
        Assert.InRange(Assert.IsType<int>(mesh.Fields["NodeCount"]), 1, 1000);
        Assert.Equal(27d, mesh.Fields["West"]);
        Assert.Equal(54d, mesh.Fields["South"]);
        Assert.Equal(27.006d, mesh.Fields["East"]);
        Assert.Equal(54.006d, mesh.Fields["North"]);
        Assert.Contains(logger.Events, entry => entry.Event.Id == 6
            && Assert.IsType<int>(entry.Fields["StartConnections"]) > 0
            && Assert.IsType<int>(entry.Fields["DestinationConnections"]) > 0);
        Assert.All(logger.Events.Where(entry => entry.Event.Id == 7), entry =>
            Assert.Null(entry.Fields["Reason"]));
    }

    [Theory]
    [InlineData(false, PlanningErrorReason.NoRouteInSearchArea)]
    [InlineData(true, PlanningErrorReason.GridConnectionUnavailable)]
    public async Task FailedSearchRetainsBoundedConnectionFacts(bool blockedEndpoint, PlanningErrorReason expectedReason)
    {
        MapFeature building = blockedEndpoint
            ? TestData.Polygon(1, TerrainKind.Building, -100, -100, 500, 800)
            : TestData.Polygon(1, TerrainKind.Building, 140, -5000, 180, 5000);
        RoutingTestLogger logger = new();
        GridRoutePlanner planner = new(new MemoryData(building), new GridGenerator(), new AStarGridPathfinder(), logger);

        PlanningException error = await Assert.ThrowsAsync<PlanningException>(() => planner.PlanAsync(
            [TestData.P(50, 150), TestData.P(300, 150)], TestData.Options, default));

        Assert.Equal(expectedReason, error.Reason);
        RoutingLogEntry[] meshes = logger.Events.Where(entry => entry.Event.Id == 5).ToArray();
        Assert.Equal(2, meshes.Length);
        Assert.Equal([0, 1], meshes.Select(entry => Assert.IsType<int>(entry.Fields["Attempt"])));
        Guid calculationId = Assert.IsType<Guid>(meshes[0].Fields["CalculationId"]);
        Assert.All(logger.Events.Where(entry => entry.Event.Id >= 5), entry =>
            Assert.Equal(calculationId, entry.Fields["CalculationId"]));
        Assert.All(meshes, entry =>
            Assert.InRange(Assert.IsType<int>(entry.Fields["NodeCount"]), 1, TestData.Options.MaxNodes));
        RoutingLogEntry[] connections = logger.Events.Where(entry => entry.Event.Id == 6).ToArray();
        Assert.Equal(CostProfile.Defaults.Count * meshes.Length, connections.Length);
        Assert.All(connections, entry =>
        {
            Assert.Equal(0, entry.Fields["PortalNodeCount"]);
            Assert.Equal(0, entry.Fields["PortalGridConnections"]);
            Assert.InRange(Assert.IsType<int>(entry.Fields["RejectedConnections"]), 0, int.MaxValue);
            Assert.Equal(blockedEndpoint, Assert.IsType<int>(entry.Fields["StartConnections"]) == 0);
        });
        Assert.All(connections.Take(CostProfile.Defaults.Count), entry =>
            Assert.True(Assert.IsType<int>(entry.Fields["RejectedConnections"]) > 0));
        Assert.All(connections.Skip(CostProfile.Defaults.Count), entry =>
            Assert.Equal(blockedEndpoint, Assert.IsType<int>(entry.Fields["RejectedConnections"]) > 0));
        RoutingLogEntry[] outcomes = logger.Events.Where(entry => entry.Event.Id == 7).ToArray();
        Assert.Equal(CostProfile.Defaults.Count * meshes.Length, outcomes.Length);
        Assert.All(outcomes, entry =>
        {
            Assert.Equal(expectedReason, entry.Fields["Reason"]);
            Assert.Equal(blockedEndpoint, Assert.IsType<int>(entry.Fields["ExpandedNodes"]) == 0);
        });
        Assert.InRange(logger.Events.Count, 1, 1 + meshes.Length * (2 + CostProfile.Defaults.Count * 4));
        Assert.DoesNotContain(logger.Events, entry => entry.Event.Id is 2 or 3);
    }

    [Fact]
    public async Task RepeatedCallsKeepDiagnosticCorrelationAndCountersIsolated()
    {
        RoutingTestLogger logger = new();
        GridRoutePlanner planner = new(new MemoryData(), new GridGenerator(), new AStarGridPathfinder(), logger);
        GeoCoordinate[] points = [TestData.P(50, 150), TestData.P(260, 150)];
        await planner.PlanAsync(points, TestData.Options, default);
        RoutingLogEntry firstMesh = Assert.Single(logger.Events, entry => entry.Event.Id == 5);
        Guid firstId = Assert.IsType<Guid>(firstMesh.Fields["CalculationId"]);
        RoutingLogEntry[] firstLegs = logger.Events.Where(entry => entry.Event.Id == 7).ToArray();
        logger.Events.Clear();

        await planner.PlanAsync(points, TestData.Options, default);

        Guid secondId = Assert.IsType<Guid>(Assert.Single(logger.Events, entry => entry.Event.Id == 5).Fields["CalculationId"]);
        Assert.NotEqual(firstId, secondId);
        Assert.All(logger.Events.Where(entry => entry.Event.Id >= 5), entry => Assert.Equal(secondId, entry.Fields["CalculationId"]));
        Assert.Equal(firstLegs.Select(entry => entry.Fields["ExpandedNodes"]),
            logger.Events.Where(entry => entry.Event.Id == 7).Select(entry => entry.Fields["ExpandedNodes"]));
        Assert.Equal(firstLegs.Select(entry => entry.Fields["EdgeEvaluations"]),
            logger.Events.Where(entry => entry.Event.Id == 7).Select(entry => entry.Fields["EdgeEvaluations"]));
    }

    private sealed class CancellingPathfinder(CancellationTokenSource cancellation) : IGridPathfinder
    {
        private bool _hasCancelled;

        public IReadOnlyList<int>? FindPath(IRoutingGraph graph, CancellationToken cancellationToken)
        {
            if (!_hasCancelled)
            {
                _hasCancelled = true;
                cancellation.Cancel();
            }
            return new AStarGridPathfinder().FindPath(graph, cancellationToken);
        }
    }
}
