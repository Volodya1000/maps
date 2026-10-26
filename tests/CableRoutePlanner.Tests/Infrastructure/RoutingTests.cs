using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using CableRoutePlanner.Infrastructure.Routing;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Xunit;
using Xunit.Abstractions;

namespace CableRoutePlanner.Tests;

public sealed class RoutingTests(ITestOutputHelper output)
{
    [Fact]
    public void ThirtyKilometreGridSearchRecordsCostAndResources()
    {
        var origin = new GeoCoordinate(54, 27);
        var projection = new LocalProjection(origin);
        var start = projection.Forward(origin);
        var finish = new Coordinate(30_000, 500);
        var end = projection.Inverse(finish);
        var options = new RoutingOptions(200, 30_000);
        var mesh = new GridGenerator().Generate(GeoBounds.Around([origin, end]), options, projection);
        var edges = 0;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        process.Refresh();
        var initialMemory = process.WorkingSet64;
        var timer = System.Diagnostics.Stopwatch.StartNew();

        var path = new AStarGridPathfinder().FindPath(start, finish, mesh, CostProfile.Defaults[0],
            (from, to) => from.Distance(to),
            (from, to) =>
            {
                edges++;
                return mesh.Nodes[from].Distance(mesh.Nodes[to]);
            }, default);
        timer.Stop();
        process.Refresh();
        var length = path.Zip(path.Skip(1), (from, to) => from.Distance(to)).Sum();
        output.WriteLine($"long-grid: length={length:F1}m cost={length:F1} nodes={mesh.Nodes.Length} "
            + $"edgesChecked={edges} elapsedMs={timer.ElapsedMilliseconds} "
            + $"workingSetDeltaMiB={Math.Max(0, process.WorkingSet64 - initialMemory) / 1048576d:F1}");
        Assert.True(length >= start.Distance(finish));
        Assert.True(double.IsFinite(length));
        Assert.True(edges > 0);
    }

    [Fact]
    public void DirectEdgeRemainsUsableWhenGridEndpointsAreDisconnected()
    {
        var start = new Coordinate(1, 0);
        var finish = new Coordinate(2, 0);
        var mesh = new GridMesh([new Coordinate(100, 100)], 1, 1, new Coordinate(100, 100), 10);
        var path = new AStarGridPathfinder().FindPath(
            start, finish, mesh, CostProfile.Defaults[0],
            (from, to) => from == start && to == finish ? 1d : null,
            (_, _) => null, default);
        Assert.Equal([start, finish], path);
    }

    [Fact]
    public void GridGeneratorBuildsExpectedMeshAndHonoursNodeLimit()
    {
        // Arrange
        var bounds = new GeoBounds(27, 54, 27.006, 54.006);
        var projection = new LocalProjection(new GeoCoordinate(54.003, 27.003));
        var generator = new GridGenerator();

        // Act
        var mesh = generator.Generate(bounds, new RoutingOptions(100, 100), projection);

        // Assert
        Assert.Equal(mesh.Columns * mesh.Rows, mesh.Nodes.Length);
        Assert.True(mesh.Columns > 1);
        Assert.True(mesh.Rows > 1);
        Assert.Throws<PlanningException>(
            () => generator.Generate(bounds, new RoutingOptions(5, 100), projection));
    }

    [Fact]
    public async Task RepeatedPlanningPreservesObstacleAvoidanceAndMetrics()
    {
        // Arrange
        var obstacle = TestData.Polygon(1, TerrainKind.Marsh, 120, 80, 180, 240);
        var planner = TestData.Planner(obstacle);
        var points = new[] { TestData.P(50, 150), TestData.P(280, 150) };

        // Act
        var first = await planner.PlanAsync(points, TestData.Options, default);
        var repeated = await planner.PlanAsync(points, TestData.Options, default);

        // Assert
        Assert.Equal(first.Count, repeated.Count);
        for (var index = 0; index < first.Count; index++)
        {
            Assert.Equal(first[index].Geometry, repeated[index].Geometry);
            Assert.Equal(first[index].Metrics, repeated[index].Metrics);
        }
    }

    [Fact]
    public async Task ArbitraryEndpointsAndOrderedWaypointsArePreserved()
    {
        var points = new[]
        {
            TestData.P(13, 17),
            TestData.P(145, 247),
            TestData.P(293, 33)
        };
        var routes = await TestData.Planner().PlanAsync(points, TestData.Options, default);
        Assert.NotEmpty(routes);
        foreach (var route in routes)
        {
            Assert.True(route.Geometry[0].DistanceTo(points[0]) < 0.0001);
            Assert.True(route.Geometry[^1].DistanceTo(points[^1]) < 0.0001);
            Assert.Contains(route.Geometry, p => p.DistanceTo(points[1]) < 0.0001);
            var measured = route.Geometry.Zip(route.Geometry.Skip(1), (a, b) => a.DistanceTo(b)).Sum();
            Assert.Equal(measured, route.Metrics.LengthMetres, 5);
        }
    }

    [Theory]
    [InlineData(TerrainKind.Marsh)]
    [InlineData(TerrainKind.Lake)]
    [InlineData(TerrainKind.River)]
    public async Task ThinFullHeightBarrierCannotBeSkippedByGrid(TerrainKind kind)
    {
        var obstacle = TestData.Polygon(1, kind, 151, -20, 153, 700);
        await Assert.ThrowsAsync<PlanningException>(
            () => TestData.Planner(obstacle).PlanAsync(
                [TestData.P(50, 150), TestData.P(260, 150)],
                TestData.Options,
                default));
    }

    [Fact]
    public async Task MarshIsAvoidedAndEveryOutputSegmentIsValidated()
    {
        var obstacle = TestData.Polygon(1, TerrainKind.Marsh, 120, 80, 180, 240);
        var routes = await TestData.Planner(obstacle).PlanAsync(
            [TestData.P(50, 150), TestData.P(280, 150)],
            TestData.Options,
            default);
        var polygon = new WKBReader().Read(obstacle.Wkb);
        Assert.NotEmpty(routes);
        foreach (var route in routes)
        {
            for (var i = 1; i < route.Geometry.Count; i++)
            {
                static Coordinate C(GeoCoordinate p) => new(p.Longitude, p.Latitude);
                Assert.False(polygon.Intersects(new LineString([C(route.Geometry[i - 1]), C(route.Geometry[i])])));
            }
        }
    }

    [Fact]
    public async Task NearbyBridgeCandidateAllowsRouteAndRemainsUnapproved()
    {
        var river = TestData.Polygon(1, TerrainKind.River, 151, -20, 153, 700);
        var bridge = TestData.Polygon(2, TerrainKind.BridgeCandidate, 140, 140, 165, 180);
        var routes = await TestData.Planner(river, bridge).PlanAsync(
                [TestData.P(50, 150), TestData.P(260, 150)],
                TestData.Options,
                default);

        Assert.NotEmpty(routes);
        Assert.All(routes, route =>
        {
            Assert.Equal(0, route.Metrics.ApprovedWaterCrossings);
            Assert.Equal(1, route.Metrics.PotentialBridgeCrossings);
        });
    }

    [Fact]
    public async Task RouteFollowsBothEndsOfMappedBridgeAcrossRiver()
    {
        var river = TestData.Polygon(1, TerrainKind.River, 151, -20, 153, 700);
        var bridge = TestData.Line(
            2,
            TerrainKind.BridgeCandidate,
            (140, 150),
            (150, 150),
            (165, 150));
        var routes = await TestData.Planner(river, bridge).PlanAsync(
            [TestData.P(50, 150), TestData.P(260, 150)],
            TestData.Options,
            default);
        var bridgeStart = TestData.P(140, 150);
        var bridgeMiddle = TestData.P(150, 150);
        var bridgeEnd = TestData.P(165, 150);
        var shortest = Assert.Single(routes, route => route.Name == "Короче");

        Assert.NotEmpty(routes);
        Assert.Equal("Короче", shortest.Name);
        Assert.Equal(shortest.Metrics.LengthMetres, shortest.Metrics.WeightedCost, 5);
        Assert.True(shortest.Geometry[0].DistanceTo(TestData.P(50, 150)) < 0.0001);
        Assert.True(shortest.Geometry[^1].DistanceTo(TestData.P(260, 150)) < 0.0001);
        Assert.Contains(shortest.Geometry, point => point.DistanceTo(bridgeStart) < 0.5);
        Assert.Contains(shortest.Geometry, point => point.DistanceTo(bridgeMiddle) < 0.5);
        Assert.Contains(shortest.Geometry, point => point.DistanceTo(bridgeEnd) < 0.5);
        Assert.Equal(1, shortest.Metrics.PotentialBridgeCrossings);
        Assert.Equal(0, shortest.Metrics.ApprovedWaterCrossings);
        Assert.InRange(shortest.Metrics.LengthMetres, 209.5, 211);
        Assert.NotEmpty(shortest.TransitionPoints!);
        var routeLine = new LineString(shortest.Geometry
            .Select(coordinate => new Coordinate(coordinate.Longitude, coordinate.Latitude)).ToArray());
        Assert.All(shortest.TransitionPoints!, transition =>
            Assert.True(routeLine.Distance(new Point(transition.Longitude, transition.Latitude)) < 0.000001));
    }

    [Fact]
    public async Task RoadNearRiverWithoutCrossingItDoesNotOpenTheWaterObstacle()
    {
        var river = TestData.Polygon(1, TerrainKind.River, 151, -20, 153, 700);
        var parallelRoad = TestData.Polygon(2, TerrainKind.BridgeCandidate, 160, 100, 170, 200);

        await Assert.ThrowsAsync<PlanningException>(
            () => TestData.Planner(river, parallelRoad).PlanAsync(
                [TestData.P(50, 150), TestData.P(260, 150)],
                TestData.Options,
                default));
    }

    [Fact]
    public async Task ReviewedCrossingAllowsRiverAndIsReported()
    {
        var river = TestData.Polygon(1, TerrainKind.River, 151, -20, 153, 700);
        var crossing = TestData.Polygon(2, TerrainKind.ApprovedCrossing, 140, 110, 165, 210, "permit-test-001");
        var routes = await TestData.Planner(river, crossing).PlanAsync(
            [TestData.P(50, 150), TestData.P(260, 150)],
            TestData.Options,
            default);
        Assert.NotEmpty(routes);
        Assert.All(routes, r => Assert.Equal(1, r.Metrics.ApprovedWaterCrossings));
    }

    [Fact]
    public async Task MissingApprovalNeverOpensRiver()
    {
        var river = TestData.Polygon(1, TerrainKind.River, 151, -20, 153, 700);
        var crossing = TestData.Polygon(2, TerrainKind.ApprovedCrossing, 140, 110, 165, 210);
        await Assert.ThrowsAsync<PlanningException>(
            () => TestData.Planner(river, crossing).PlanAsync(
                [TestData.P(50, 150), TestData.P(260, 150)],
                TestData.Options,
                default));
    }

    [Fact]
    public async Task CostProfilesProduceDifferentActualGeometry()
    {
        var forest = TestData.Polygon(1, TerrainKind.Forest, 100, 60, 220, 240);
        var routes = await TestData.Planner(forest).PlanAsync(
            [TestData.P(30, 150), TestData.P(300, 150)],
            TestData.Options,
            default);
        Assert.True(routes.Count >= 2);
        Assert.True(routes.Min(r => r.Metrics.ForestMetres) < routes.Max(r => r.Metrics.ForestMetres));
        Assert.All(routes, r => Assert.Null(r.Metrics.AscentMetres));
    }

    [Fact]
    public async Task PassableDirectRoadCrossingCanLoseToALongerCheaperDetour()
    {
        var road = TestData.Line(1, TerrainKind.Road, (150, 100), (150, 200));
        var routes = await TestData.Planner(road).PlanAsync(
            [TestData.P(50, 150), TestData.P(250, 150)],
            TestData.Options,
            default);

        Assert.Contains(routes, route => route.Name == "Короче" && route.Metrics.RoadCrossings == 1);
        Assert.Contains(routes, route => route.Name == "Баланс" && route.Metrics.RoadCrossings == 0);
    }

    [Fact]
    public async Task CostReducingClearingDetourIsSearchedEvenWhenDirectSegmentIsClear()
    {
        var clearing = TestData.Polygon(1, TerrainKind.Clearing, 100, 150, 300, 300);
        var routes = await TestData.Planner(clearing).PlanAsync(
            [TestData.P(50, 350), TestData.P(350, 350)],
            TestData.Options,
            default);

        var balanced = Assert.Single(routes, route => route.Name == "Баланс");
        Assert.True(balanced.Metrics.LengthMetres > TestData.P(50, 350).DistanceTo(TestData.P(350, 350)));
        Assert.True(balanced.Metrics.WeightedCost < TestData.P(50, 350).DistanceTo(TestData.P(350, 350)));
        var clearingGeometry = new WKBReader().Read(clearing.Wkb);
        Assert.Contains(balanced.Geometry, point => clearingGeometry.Covers(
            new Point(point.Longitude, point.Latitude)));
    }

    [Fact]
    public async Task IdenticalGeometryIsNotPresentedAsThreeAlternatives()
    {
        var routes = await TestData.Planner().PlanAsync(
            [TestData.P(50, 150), TestData.P(260, 150)],
            TestData.Options,
            default);
        Assert.Single(routes);
    }

    [Fact]
    public async Task CancellationAndMemoryLimitsAreEnforced()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TestData.Planner().PlanAsync(
                [TestData.P(50, 150), TestData.P(260, 150)],
                TestData.Options,
                cts.Token));
        await Assert.ThrowsAsync<PlanningException>(
            () => TestData.Planner().PlanAsync([TestData.P(10, 10), TestData.P(300, 600)], new(5, 100), default));
    }

    [Fact]
    public async Task OutsideCoverageAndCoincidentPointsReturnActionableErrors()
    {
        await Assert.ThrowsAsync<PlanningException>(
            () => TestData.Planner().PlanAsync([new(52, 25), TestData.P(200, 150)], TestData.Options, default));
        await Assert.ThrowsAsync<PlanningException>(
            () => TestData.Planner().PlanAsync(
                [TestData.P(50, 150), TestData.P(50, 150)],
                TestData.Options,
                default));
    }
}
