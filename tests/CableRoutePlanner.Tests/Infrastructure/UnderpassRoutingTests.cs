using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using CableRoutePlanner.Infrastructure.Routing;
using CableRoutePlanner.Persistence;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class UnderpassRoutingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SharedUnderpassJunctionOnRoadAxisConnectsThreeConfirmedBranches(bool connected)
    {
        MapFeature road = TestData.Line(1, TerrainKind.Road, (-500, 150), (1000, 150));
        MapFeature lower = TestData.Line(2, TerrainKind.PedestrianUnderpassCandidate, (160, 130), (160, 150)) with
        { UnderpassRoadIds = new long[] { 1 }, UnderpassTopology = new(2, [10, 11], [10]) };
        MapFeature upper = TestData.Line(3, TerrainKind.PedestrianUnderpassCandidate, (160, 150), (160, 170)) with
        { UnderpassRoadIds = new long[] { 1 }, UnderpassTopology = new(connected ? 2 : 3, [connected ? 11 : 20, 12], [12]) };
        MapFeature branch = TestData.Line(4, TerrainKind.PedestrianUnderpassCandidate, (160, 150), (200, 170)) with
        { UnderpassRoadIds = new long[] { 1 }, UnderpassTopology = new(connected ? 2 : 4, [connected ? 11 : 21, 13], [13]) };
        IRoutePlanner planner = TestData.Planner(road, lower, upper, branch);
        GeoCoordinate[] points = [TestData.P(160, 120), TestData.P(200, 190)];
        RoutingOptions options = TestData.Options with { AllowExperimentalPedestrianUnderpasses = true };
        if (!connected)
        {
            PlanningException error = await Assert.ThrowsAsync<PlanningException>(() => planner.PlanAsync(points, options, default));
            Assert.Equal(PlanningErrorReason.NoRouteInSearchArea, error.Reason);
            return;
        }
        RouteAlternative route = Assert.Single(await planner.PlanAsync(points, options, default));
        Assert.Single(route.UnderpassPassages!);
        Assert.Single(route.RoadCrossingEvents!);
        Assert.Contains(route.Geometry, point => point.DistanceTo(TestData.P(160, 150)) < 1);
    }

    [Fact]
    public async Task NetworkWithoutConfirmedSurfaceEntrancesCannotBeEnteredAtRoadContact()
    {
        MapFeature road = TestData.Line(1, TerrainKind.Road, (-500, 150), (1000, 150));
        MapFeature tunnel = TestData.Line(2, TerrainKind.PedestrianUnderpassCandidate, (160, 130), (160, 170)) with
        { UnderpassRoadIds = new long[] { 1 }, UnderpassTopology = new(2, [10, 11], []) };
        PlanningException error = await Assert.ThrowsAsync<PlanningException>(() => TestData.Planner(road, tunnel).PlanAsync(
            [TestData.P(160, 140), TestData.P(160, 160)], TestData.Options with { AllowExperimentalPedestrianUnderpasses = true }, default));
        Assert.Equal(PlanningErrorReason.NoRouteInSearchArea, error.Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyConfirmedNetworkContinuationCoversRoadReviewWindow(bool connected)
    {
        MapFeature road = TestData.Line(1, TerrainKind.Road, (-100, 150), (700, 150));
        MapFeature first = TestData.Line(2, TerrainKind.PedestrianUnderpassCandidate, (160, 130), (160, 151)) with
        { UnderpassRoadIds = new long[] { 1 }, UnderpassTopology = new(2, [10, 11], [10]) };
        MapFeature second = TestData.Line(3, TerrainKind.PedestrianUnderpassCandidate, (160, 151), (160, 170)) with
        { UnderpassTopology = new(connected ? 2 : 3, connected ? new long[] { 11, 12 } : new long[] { 20, 21 }, [connected ? 12 : 21]) };
        LocalProjection projection = new(TestData.P(0, 0));
        TerrainEvaluator evaluator = new([road, first, second], projection, allowUnderpasses: true);
        Assert.Equal(connected, evaluator.Evaluate(projection.Forward(TestData.P(160, 140)),
            projection.Forward(TestData.P(160, 160))) is not null);
    }

    private static readonly System.Text.Json.JsonSerializerOptions FixtureJson = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    internal static MapFeature[] VictoryFeatures()
    {
        return System.Text.Json.JsonSerializer.Deserialize<MapFeature[]>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Underpasses", "victory.json")), FixtureJson)!;
    }

    [Fact]
    public async Task VictorySquareRealOsmNetworkConnectsOppositeEntrancesInOnePassage()
    {
        MapFeature[] features = VictoryFeatures();
        Assert.Equal(6, features.Where(feature => feature.UnderpassTopology is not null)
            .SelectMany(feature => feature.UnderpassTopology!.EntranceNodeIds).Distinct().Count());
        GeoCoordinate[] points = [new(53.9089564, 27.5742584), new(53.9081863, 27.5759061)];
        RoutingOptions options = TestData.Options with { AllowExperimentalPedestrianUnderpasses = true, MaxRouteLengthMetres = 300 };
        using SqliteGeodataFixture fixture = new(features, null, bounds: new(27.565, 53.885, 27.60, 53.915), schemaVersion: 3);
        GridRoutePlanner planner = new(fixture.Repository, new GridGenerator(), new AStarGridPathfinder(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GridRoutePlanner>.Instance);
        RouteAlternative route = Assert.Single(await planner.PlanAsync(points, options, default));
        Assert.Single(route.UnderpassPassages!);
        Assert.InRange(route.Metrics.LengthMetres, 180, 230);
        Assert.Equal(points[0], route.Geometry[0]);
        Assert.Equal(points[1], route.Geometry[^1]);
        Geometry path = new LineString(route.Geometry.Select(point => new Coordinate(point.Longitude, point.Latitude)).ToArray());
        WKBReader reader = new();
        Assert.All(features.Where(feature => feature.Kind == TerrainKind.Building), building => Assert.False(path.Intersects(reader.Read(building.Wkb))));
        PlanningException error = await Assert.ThrowsAsync<PlanningException>(() => planner.PlanAsync(points,
            options with { AllowExperimentalPedestrianUnderpasses = false }, default));
        Assert.Contains(error.Reason, new[] { PlanningErrorReason.NoRouteInSearchArea, PlanningErrorReason.RouteLengthLimitExceeded });
    }

    [Fact]
    public async Task FourEntranceRingPreservesConnectedTunnelAndAvoidsCentralBuilding()
    {
        MapFeature vertical = TestData.Line(1, TerrainKind.Road, (150, -500), (150, 1000));
        MapFeature horizontal = TestData.Line(2, TerrainKind.Road, (-500, 150), (1000, 150));
        MapFeature ring = TestData.Line(3, TerrainKind.PedestrianUnderpassCandidate,
            (130, 130), (170, 130), (170, 170), (130, 170), (130, 130)) with
        { UnderpassRoadIds = new long[] { 1, 2 }, UnderpassTopology = new(3, [10, 11, 12, 13, 10], []) };
        MapFeature[] arms =
        [
            TestData.Line(4, TerrainKind.PedestrianUnderpassCandidate, (100, 130), (130, 130)) with { UnderpassTopology = new(3, [20, 10], [20]) },
            TestData.Line(5, TerrainKind.PedestrianUnderpassCandidate, (200, 130), (170, 130)) with { UnderpassTopology = new(3, [21, 11], [21]) },
            TestData.Line(6, TerrainKind.PedestrianUnderpassCandidate, (200, 170), (170, 170)) with { UnderpassTopology = new(3, [22, 12], [22]) },
            TestData.Line(7, TerrainKind.PedestrianUnderpassCandidate, (100, 170), (130, 170)) with { UnderpassTopology = new(3, [23, 13], [23]) }
        ];
        MapFeature building = TestData.Polygon(8, TerrainKind.Building, 140, 140, 160, 160);
        IRoutePlanner planner = TestData.Planner([vertical, horizontal, ring, building, .. arms]);
        GeoCoordinate[] points = [TestData.P(100, 130), TestData.P(200, 170)];
        PlanningException error = await Assert.ThrowsAsync<PlanningException>(() => planner.PlanAsync(points, TestData.Options, default));
        Assert.Equal(PlanningErrorReason.NoRouteInSearchArea, error.Reason);
        RouteAlternative route = Assert.Single(await planner.PlanAsync(points,
            TestData.Options with { AllowExperimentalPedestrianUnderpasses = true }, default));
        Assert.Single(route.UnderpassPassages!);
        Assert.Equal(2, route.RoadCrossingEvents!.Count);
        LineString path = new(route.Geometry.Select(point => new Coordinate(point.Longitude, point.Latitude)).ToArray());
        Assert.False(path.Intersects(new WKBReader().Read(building.Wkb)));
        Assert.Contains(route.Geometry, point => point.DistanceTo(TestData.P(130, 130)) < 1
            || point.DistanceTo(TestData.P(170, 170)) < 1);
    }

    [Fact]
    public async Task PortalConnectsTwoPointsOnlyWhenEnabled()
    {
        MapFeature road = TestData.Line(1, TerrainKind.Road, (-100, 150), (700, 150));
        MapFeature tunnel = TestData.Line(2, TerrainKind.PedestrianUnderpassCandidate, (163, 130), (163, 170)) with { UnderpassRoadIds = new long[] { 1 } };
        IRoutePlanner planner = TestData.Planner(road, tunnel);
        GeoCoordinate[] points = [TestData.P(40, 100), TestData.P(280, 200)];
        PlanningException error = await Assert.ThrowsAsync<PlanningException>(() => planner.PlanAsync(points, TestData.Options, default));
        Assert.Equal(PlanningErrorReason.NoRouteInSearchArea, error.Reason);
        IReadOnlyList<RouteAlternative> routes = await planner.PlanAsync(points, TestData.Options with { AllowExperimentalPedestrianUnderpasses = true }, default);
        RouteAlternative route = Assert.Single(routes);
        Assert.Contains(route.RoadContacts!, contact => contact.Geometry.Any(point => point.DistanceTo(TestData.P(163, 150)) < 2));
        Assert.Equal(new long[] { 2 }, route.UnderpassCandidateIds);
        RoadCrossingEvent crossing = Assert.Single(route.RoadCrossingEvents!);
        Assert.Equal(1, crossing.RoadId);
        Assert.Equal(RoadCrossingKind.PedestrianUnderpass, crossing.Kind);
    }

    [Fact]
    public async Task LegacyUserSettingsEnableMatchedUnderpassForRoute()
    {
        using var fixture = new TemporaryDirectory();
        string path = Path.Combine(fixture.DirectoryPath, "settings.json");
        File.WriteAllText(path, "{\"AllowExperimentalPedestrianUnderpasses\":false}");
        var store = new JsonSettingsStore(path);
        MapFeature road = TestData.Line(1, TerrainKind.Road, (-100, 150), (700, 150));
        MapFeature tunnel = TestData.Line(2, TerrainKind.PedestrianUnderpassCandidate, (163, 130), (163, 170))
            with
        { UnderpassRoadIds = new long[] { 1 } };

        IReadOnlyList<RouteAlternative> routes = await TestData.Planner(road, tunnel).PlanAsync(
            [TestData.P(40, 100), TestData.P(280, 200)], store.Load(), default);

        Assert.Equal(new long[] { 2 }, Assert.Single(routes).UnderpassCandidateIds);
    }

    [Fact]
    public async Task UnderpassIsNotReportedWhenGasCandidateAlreadyPermitsTheRoadContact()
    {
        MapFeature road = TestData.Line(1, TerrainKind.Road, (-100, 150), (700, 150));
        MapFeature tunnel = TestData.Line(2, TerrainKind.PedestrianUnderpassCandidate, (163, 130), (163, 170))
            with
        { UnderpassRoadIds = new long[] { 1 } };
        MapFeature gas = TestData.Line(3, TerrainKind.GasPipelineCrossingCandidate, (163, 130), (163, 170));

        IReadOnlyList<RouteAlternative> routes = await TestData.Planner(road, tunnel, gas).PlanAsync(
            [TestData.P(40, 100), TestData.P(280, 200)],
            TestData.Options with { AllowExperimentalPedestrianUnderpasses = true },
            default);

        Assert.All(routes, route => Assert.Empty(route.UnderpassCandidateIds ?? []));
    }

    [Fact]
    public async Task BuildingTouchingSourceLineUsesShortSafeUnderpassPortals()
    {
        MapFeature road = TestData.Line(1, TerrainKind.Road, (-100, 150), (700, 150));
        MapFeature sourceLine = TestData.Line(2, TerrainKind.PedestrianUnderpassCandidate, (163, 130), (163, 175))
            with
        { UnderpassRoadIds = new long[] { 1 } };
        MapFeature building = TestData.Polygon(3, TerrainKind.Building, 150, 165, 170, 185);

        IReadOnlyList<RouteAlternative> routes = await TestData.Planner(road, sourceLine, building).PlanAsync(
            [TestData.P(40, 100), TestData.P(280, 200)],
            TestData.Options with { AllowExperimentalPedestrianUnderpasses = true },
            default);

        RouteAlternative route = Assert.Single(routes);
        Assert.Equal(new long[] { 2 }, route.UnderpassCandidateIds);
        LineString path = new(route.Geometry.Select(point => new Coordinate(point.Longitude, point.Latitude)).ToArray());
        Assert.False(path.Intersects(new WKBReader().Read(building.Wkb)));
    }

    [Fact]
    public async Task UnderpassWithoutClearEntranceDoesNotCreateARoute()
    {
        MapFeature road = TestData.Line(1, TerrainKind.Road, (-100, 150), (700, 150));
        MapFeature tunnel = TestData.Line(2, TerrainKind.PedestrianUnderpassCandidate, (163, 130), (163, 175))
            with
        { UnderpassRoadIds = new long[] { 1 } };
        MapFeature building = TestData.Polygon(3, TerrainKind.Building, 150, 151, 170, 185);

        PlanningException error = await Assert.ThrowsAsync<PlanningException>(() =>
            TestData.Planner(road, tunnel, building).PlanAsync(
                [TestData.P(40, 100), TestData.P(280, 200)],
                TestData.Options with { AllowExperimentalPedestrianUnderpasses = true }, default));

        Assert.Equal(PlanningErrorReason.NoRouteInSearchArea, error.Reason);
    }

    [Theory]
    [InlineData(false, 1, 0, 0, false)]
    [InlineData(true, 1, 0, 0, true)]
    [InlineData(true, 3, 0, 0, false)]
    [InlineData(true, 1, 10, 10, false)]
    [InlineData(true, 1, -10, 10, false)]
    public void RequiresEnabledLinkedAndAlignedCrossing(bool enabled, long linkedRoad, double startX, double endX, bool allowed)
    {
        LocalProjection projection = new(TestData.P(0, 0));
        MapFeature road = TestData.Line(1, TerrainKind.Road, (-100, 150), (700, 150));
        MapFeature tunnel = TestData.Line(2, TerrainKind.PedestrianUnderpassCandidate, (160, 130), (160, 170)) with { UnderpassRoadIds = new long[] { linkedRoad } };
        TerrainEvaluator evaluator = new([road, tunnel], projection, allowUnderpasses: enabled);
        Coordinate start = projection.Forward(TestData.P(160 + startX, 140));
        Coordinate end = projection.Forward(TestData.P(160 + endX, 160));
        Assert.Equal(allowed, evaluator.Evaluate(start, end) is not null);
    }
    [Theory]
    [InlineData(TerrainKind.River)]
    [InlineData(TerrainKind.Marsh)]
    [InlineData(TerrainKind.Building)]
    public void UnderpassDoesNotOpenBlockedTerrain(TerrainKind kind)
    {
        LocalProjection projection = new(TestData.P(0, 0));
        MapFeature road = TestData.Line(1, TerrainKind.Road, (-100, 150), (700, 150));
        MapFeature tunnel = TestData.Line(2, TerrainKind.PedestrianUnderpassCandidate, (160, 130), (160, 170)) with { UnderpassRoadIds = new long[] { 1 } };
        MapFeature obstacle = TestData.Polygon(3, kind, 150, 145, 170, 155);
        TerrainEvaluator evaluator = new([road, tunnel, obstacle], projection, allowUnderpasses: true);
        Assert.Null(evaluator.Evaluate(projection.Forward(TestData.P(160, 140)), projection.Forward(TestData.P(160, 160))));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryRoadNeedsItsOwnLink(bool includeSecondRoad)
    {
        LocalProjection projection = new(TestData.P(0, 0));
        MapFeature first = TestData.Line(1, TerrainKind.Road, (-100, 145), (700, 145));
        MapFeature second = TestData.Line(3, TerrainKind.Road, (-100, 155), (700, 155));
        MapFeature tunnel = TestData.Line(2, TerrainKind.PedestrianUnderpassCandidate, (160, 130), (160, 170)) with { UnderpassRoadIds = includeSecondRoad ? new long[] { 1, 3 } : new long[] { 1 } };
        TerrainEvaluator evaluator = new([first, second, tunnel], projection, allowUnderpasses: true);
        Assert.Equal(includeSecondRoad, evaluator.Evaluate(projection.Forward(TestData.P(160, 135)), projection.Forward(TestData.P(160, 165))) is not null);
    }
}
