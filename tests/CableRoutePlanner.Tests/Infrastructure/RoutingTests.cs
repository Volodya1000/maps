using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using CableRoutePlanner.Infrastructure.Routing;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace CableRoutePlanner.Tests;

public sealed class RoutingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SearchMarginKeepsTheSameMetricReserveOnBothAxes()
    {
        BoundsRecordingData data = new();
        GridRoutePlanner planner = new(data, new GridGenerator(), new DirectPathfinder(),
            NullLogger<GridRoutePlanner>.Instance);
        GeoCoordinate start = new(54, 27);
        GeoCoordinate end = new(54, 27.01);

        await planner.PlanAsync([start, end], new(200, 100_000), default);

        Assert.NotNull(data.LastBounds);
        GeoBounds bounds = data.LastBounds!.Value;
        double latitudeReserve = (start.Latitude - bounds.South) * GeoCoordinate.MeanEarthRadiusMetres * Math.PI / 180;
        double longitudeReserve = (start.Longitude - bounds.West)
            * GeoCoordinate.MeanEarthRadiusMetres * Math.Cos(start.Latitude * Math.PI / 180) * Math.PI / 180;
        Assert.InRange(latitudeReserve, 999, 1001);
        Assert.InRange(longitudeReserve, 999, 1001);
        Assert.True(data.Info.Bounds.Contains(bounds));
    }

    [Fact]
    public async Task SearchMarginIsCappedAtFiveKilometresAndStaysInCoverage()
    {
        BoundsRecordingData data = new();
        GridRoutePlanner planner = new(data, new GridGenerator(), new DirectPathfinder(),
            NullLogger<GridRoutePlanner>.Instance);
        GeoCoordinate start = new(54, 27);
        GeoCoordinate end = new(54, 27.8);

        await planner.PlanAsync([start, end], new(200, 100_000), default);

        Assert.NotNull(data.LastBounds);
        GeoBounds bounds = data.LastBounds!.Value;
        double latitudeReserve = (start.Latitude - bounds.South) * GeoCoordinate.MeanEarthRadiusMetres * Math.PI / 180;
        double longitudeReserve = (start.Longitude - bounds.West)
            * GeoCoordinate.MeanEarthRadiusMetres * Math.Cos(start.Latitude * Math.PI / 180) * Math.PI / 180;
        Assert.InRange(latitudeReserve, 4_999, 5_001);
        Assert.InRange(longitudeReserve, 4_999, 5_001);
        Assert.True(data.Info.Bounds.Contains(bounds));
    }

    [Fact]
    public async Task SearchMarginIsClippedToCoverageBeforeQuery()
    {
        BoundsRecordingData data = new(new(26.999, 53.999, 27.02, 54.02));
        GridRoutePlanner planner = new(data, new GridGenerator(), new DirectPathfinder(),
            NullLogger<GridRoutePlanner>.Instance);
        GeoCoordinate start = new(54.001, 27.001);
        GeoCoordinate end = new(54.005, 27.005);

        IReadOnlyList<RouteAlternative> routes = await planner.PlanAsync(
            [start, end],
            new(200, 100_000),
            default);

        Assert.NotEmpty(routes);
        Assert.NotNull(data.LastBounds);
        GeoBounds bounds = data.LastBounds!.Value;
        Assert.Equal(data.Info.Bounds.West, bounds.West, 12);
        Assert.Equal(data.Info.Bounds.South, bounds.South, 12);
        Assert.True(bounds.East < data.Info.Bounds.East);
        Assert.True(bounds.North < data.Info.Bounds.North);
    }

    [Fact]
    public void BridgeAlignmentChoosesLongerSpliceWhenItsProfileCostIsLower()
    {
        MapFeature river = TestData.Polygon(1, TerrainKind.River, 99, -100, 101, 100);
        MapFeature bridge = TestData.Line(2, TerrainKind.BridgeCandidate, (80, 0), (120, 0));
        MapFeature road = TestData.Line(3, TerrainKind.Road, (20, 22), (20, 23));
        LocalProjection projection = new(TestData.P(0, 0));
        TerrainEvaluator evaluator = new(
            [river, bridge, road],
            projection,
            20);
        CostProfile profile = new("road-averse", 0, 0, 500, 1);
        Coordinate[] route =
        [
            new(0, 30),
            new(20, 30),
            new(40, 5),
            new(160, 5),
            new(180, 30),
            new(200, 30)
        ];

        List<Coordinate> aligned = evaluator.AlignBridgeCrossings(route, profile);

        Assert.Contains(aligned, point => point.Distance(route[1]) < 0.5);
        for (int index = 1; index < aligned.Count; index++)
        {
            Assert.NotNull(evaluator.Evaluate(aligned[index - 1], aligned[index]));
        }

        double cost = aligned
            .Zip(aligned.Skip(1), (from, to) => evaluator.Evaluate(from, to)!.Cost(profile))
            .Sum();
        Assert.True(cost < 1_000);
    }

    [Fact]
    public void ThirtyKilometreGridSearchRecordsCostAndResources()
    {
        var origin = new GeoCoordinate(54, 27);
        var projection = new LocalProjection(origin);
        Coordinate start = projection.Forward(origin);
        var finish = new Coordinate(30_000, 500);
        GeoCoordinate end = projection.Inverse(finish);
        var options = new RoutingOptions(200, 30_000);
        GridMesh mesh = new GridGenerator().Generate(GeoBounds.Around([origin, end]), options, projection);
        int edges = 0;
        using System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess();
        process.Refresh();
        long initialMemory = process.WorkingSet64;
        System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();

        IReadOnlyList<Coordinate> path = new AStarGridPathfinder().FindPath(start, finish, mesh, CostProfile.Defaults[0],
            (from, to) => from.Distance(to),
            (from, to) =>
            {
                edges++;
                return mesh.Nodes[from].Distance(mesh.Nodes[to]);
            }, default);
        timer.Stop();
        process.Refresh();
        double length = path.Zip(path.Skip(1), (from, to) => from.Distance(to)).Sum();
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
        IReadOnlyList<Coordinate> path = new AStarGridPathfinder().FindPath(
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
        GridMesh mesh = generator.Generate(bounds, new RoutingOptions(100, 100), projection);

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
        MapFeature obstacle = TestData.Polygon(1, TerrainKind.Marsh, 120, 80, 180, 240);
        IRoutePlanner planner = TestData.Planner(obstacle);
        GeoCoordinate[] points = new[] { TestData.P(50, 150), TestData.P(280, 150) };

        // Act
        IReadOnlyList<RouteAlternative> first = await planner.PlanAsync(points, TestData.Options, default);
        IReadOnlyList<RouteAlternative> repeated = await planner.PlanAsync(points, TestData.Options, default);

        // Assert
        Assert.Equal(first.Count, repeated.Count);
        for (int index = 0; index < first.Count; index++)
        {
            Assert.Equal(first[index].Geometry, repeated[index].Geometry);
            Assert.Equal(first[index].Metrics, repeated[index].Metrics);
        }
    }

    [Fact]
    public async Task ArbitraryEndpointsAndOrderedWaypointsArePreserved()
    {
        GeoCoordinate[] points = new[]
        {
            TestData.P(13, 17),
            TestData.P(145, 247),
            TestData.P(293, 33)
        };
        IReadOnlyList<RouteAlternative> routes = await TestData.Planner().PlanAsync(points, TestData.Options, default);
        Assert.NotEmpty(routes);
        foreach (RouteAlternative route in routes)
        {
            Assert.True(route.Geometry[0].DistanceTo(points[0]) < 0.0001);
            Assert.True(route.Geometry[^1].DistanceTo(points[^1]) < 0.0001);
            Assert.Contains(route.Geometry, p => p.DistanceTo(points[1]) < 0.0001);
            double measured = route.Geometry.Zip(route.Geometry.Skip(1), (a, b) => a.DistanceTo(b)).Sum();
            Assert.Equal(measured, route.Metrics.LengthMetres, 5);
        }
    }

    [Theory]
    [InlineData(TerrainKind.Marsh)]
    [InlineData(TerrainKind.Lake)]
    [InlineData(TerrainKind.River)]
    public async Task ThinFullHeightBarrierCannotBeSkippedByGrid(TerrainKind kind)
    {
        MapFeature obstacle = TestData.Polygon(1, kind, 151, -20, 153, 700);
        await Assert.ThrowsAsync<PlanningException>(
            () => TestData.Planner(obstacle).PlanAsync(
                [TestData.P(50, 150), TestData.P(260, 150)],
                TestData.Options,
                default));
    }

    [Fact]
    public async Task MarshIsAvoidedAndEveryOutputSegmentIsValidated()
    {
        MapFeature obstacle = TestData.Polygon(1, TerrainKind.Marsh, 120, 80, 180, 240);
        IReadOnlyList<RouteAlternative> routes = await TestData.Planner(obstacle).PlanAsync(
            [TestData.P(50, 150), TestData.P(280, 150)],
            TestData.Options,
            default);
        Geometry polygon = new WKBReader().Read(obstacle.Wkb);
        Assert.NotEmpty(routes);
        foreach (RouteAlternative route in routes)
        {
            for (int i = 1; i < route.Geometry.Count; i++)
            {
                static Coordinate C(GeoCoordinate p) => new(p.Longitude, p.Latitude);
                Assert.False(polygon.Intersects(new LineString([C(route.Geometry[i - 1]), C(route.Geometry[i])])));
            }
        }
    }

    [Fact]
    public async Task NearbyBridgeCandidateAllowsRouteAndRemainsUnapproved()
    {
        MapFeature river = TestData.Polygon(1, TerrainKind.River, 151, -20, 153, 700);
        MapFeature bridge = TestData.Polygon(2, TerrainKind.BridgeCandidate, 140, 140, 165, 180);
        IReadOnlyList<RouteAlternative> routes = await TestData.Planner(river, bridge).PlanAsync(
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
        MapFeature river = TestData.Polygon(1, TerrainKind.River, 151, -20, 153, 700);
        MapFeature bridge = TestData.Line(
            2,
            TerrainKind.BridgeCandidate,
            (140, 150),
            (150, 150),
            (165, 150));
        IReadOnlyList<RouteAlternative> routes = await TestData.Planner(river, bridge).PlanAsync(
            [TestData.P(50, 150), TestData.P(260, 150)],
            TestData.Options,
            default);
        GeoCoordinate bridgeStart = TestData.P(140, 150);
        GeoCoordinate bridgeMiddle = TestData.P(150, 150);
        GeoCoordinate bridgeEnd = TestData.P(165, 150);
        RouteAlternative shortest = Assert.Single(routes, route => route.Name == "Короче");

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
        Assert.InRange(shortest.TransitionPoints!.Count, 1, 2);
        var routeLine = new LineString(shortest.Geometry
            .Select(coordinate => new Coordinate(coordinate.Longitude, coordinate.Latitude)).ToArray());
        Assert.All(shortest.TransitionPoints!, transition =>
            Assert.True(routeLine.Distance(new Point(transition.Longitude, transition.Latitude)) < 0.000001));
    }

    [Fact]
    public async Task RoadNearRiverWithoutCrossingItDoesNotOpenTheWaterObstacle()
    {
        MapFeature river = TestData.Polygon(1, TerrainKind.River, 151, -20, 153, 700);
        MapFeature parallelRoad = TestData.Polygon(2, TerrainKind.BridgeCandidate, 160, 100, 170, 200);

        await Assert.ThrowsAsync<PlanningException>(
            () => TestData.Planner(river, parallelRoad).PlanAsync(
                [TestData.P(50, 150), TestData.P(260, 150)],
                TestData.Options,
                default));
    }

    [Fact]
    public async Task ReviewedCrossingAllowsRiverAndIsReported()
    {
        MapFeature river = TestData.Polygon(1, TerrainKind.River, 151, -20, 153, 700);
        MapFeature crossing = TestData.Polygon(2, TerrainKind.ApprovedCrossing, 140, 110, 165, 210, "permit-test-001");
        IReadOnlyList<RouteAlternative> routes = await TestData.Planner(river, crossing).PlanAsync(
            [TestData.P(50, 150), TestData.P(260, 150)],
            TestData.Options,
            default);
        Assert.NotEmpty(routes);
        Assert.All(routes, r => Assert.Equal(1, r.Metrics.ApprovedWaterCrossings));
    }

    [Fact]
    public async Task MissingApprovalNeverOpensRiver()
    {
        MapFeature river = TestData.Polygon(1, TerrainKind.River, 151, -20, 153, 700);
        MapFeature crossing = TestData.Polygon(2, TerrainKind.ApprovedCrossing, 140, 110, 165, 210);
        await Assert.ThrowsAsync<PlanningException>(
            () => TestData.Planner(river, crossing).PlanAsync(
                [TestData.P(50, 150), TestData.P(260, 150)],
                TestData.Options,
                default));
    }

    [Fact]
    public async Task CostProfilesProduceDifferentActualGeometry()
    {
        MapFeature forest = TestData.Polygon(1, TerrainKind.Forest, 100, 60, 220, 240);
        IReadOnlyList<RouteAlternative> routes = await TestData.Planner(forest).PlanAsync(
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
        MapFeature road = TestData.Line(1, TerrainKind.Road, (150, 100), (150, 200));
        IReadOnlyList<RouteAlternative> routes = await TestData.Planner(road).PlanAsync(
            [TestData.P(50, 150), TestData.P(250, 150)],
            TestData.Options,
            default);

        Assert.Contains(routes, route => route.Name == "Короче" && route.Metrics.RoadCrossings == 1);
        Assert.Contains(routes, route => route.Name == "Баланс" && route.Metrics.RoadCrossings == 0);
    }

    [Fact]
    public async Task CostReducingClearingDetourIsSearchedEvenWhenDirectSegmentIsClear()
    {
        MapFeature clearing = TestData.Polygon(1, TerrainKind.Clearing, 100, 150, 300, 300);
        IReadOnlyList<RouteAlternative> routes = await TestData.Planner(clearing).PlanAsync(
            [TestData.P(50, 350), TestData.P(350, 350)],
            TestData.Options,
            default);

        RouteAlternative balanced = Assert.Single(routes, route => route.Name == "Баланс");
        Assert.True(balanced.Metrics.LengthMetres > TestData.P(50, 350).DistanceTo(TestData.P(350, 350)));
        Assert.True(balanced.Metrics.WeightedCost < TestData.P(50, 350).DistanceTo(TestData.P(350, 350)));
        Geometry clearingGeometry = new WKBReader().Read(clearing.Wkb);
        Assert.Contains(balanced.Geometry, point => clearingGeometry.Covers(
            new Point(point.Longitude, point.Latitude)));
    }

    [Fact]
    public async Task IdenticalGeometryIsNotPresentedAsThreeAlternatives()
    {
        IReadOnlyList<RouteAlternative> routes = await TestData.Planner().PlanAsync(
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

    private sealed class BoundsRecordingData : IGeodataRepository
    {
        public DataPackageInfo Info { get; }

        public BoundsRecordingData(GeoBounds? coverage = null)
        {
            Info = new(
                "bounds-test",
                coverage ?? new(20, 50, 35, 60),
                "tests",
                false,
                "tests");
        }

        public GeoBounds? LastBounds { get; private set; }

        public Task<IReadOnlyList<MapFeature>> QueryAsync(GeoBounds bounds, CancellationToken cancellationToken)
        {
            LastBounds = bounds;
            return Task.FromResult<IReadOnlyList<MapFeature>>([]);
        }
    }

    private sealed class DirectPathfinder : IGridPathfinder
    {
        public IReadOnlyList<Coordinate> FindPath(
            Coordinate start,
            Coordinate destination,
            GridMesh mesh,
            CostProfile profile,
            Func<Coordinate, Coordinate, double?> evaluate,
            Func<int, int, double?> gridEdgeCost,
            CancellationToken cancellationToken) => [start, destination];
    }
}
