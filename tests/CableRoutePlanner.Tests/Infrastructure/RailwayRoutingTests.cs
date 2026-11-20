using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using CableRoutePlanner.Infrastructure.Routing;
using CableRoutePlanner.Persistence;
using NetTopologySuite.Geometries;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class RailwayRoutingTests
{
    private static TerrainKind Railway => Enum.Parse<TerrainKind>("Railway");

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task TracksAcrossDirectRouteDoNotChangeItsGeometry(int trackCount)
    {
        MapFeature[] tracks = Enumerable.Range(0, trackCount)
            .Select(index => TestData.Line(index + 1, Railway, (100 + index * 20, 0), (100 + index * 20, 300)))
            .ToArray();
        GeoCoordinate[] endpoints = [TestData.P(50, 150), TestData.P(250, 150)];
        IReadOnlyList<RouteAlternative> baseline = await TestData.Planner().PlanAsync(endpoints, TestData.Options, default);
        IReadOnlyList<RouteAlternative> actual = await TestData.Planner(tracks).PlanAsync(endpoints, TestData.Options, default);

        Assert.NotEmpty(actual);
        Assert.Equal(baseline[0].Geometry, actual[0].Geometry);
        Assert.Equal(0, actual[0].Metrics.RoadCrossings);
    }

    [Fact]
    public void RailwayDoesNotAuthorizeNearbyOrCoincidentRoadCrossing()
    {
        LocalProjection projection = new(TestData.P(0, 0));
        MapFeature railway = TestData.Line(1, Railway, (100, 0), (100, 300));
        MapFeature road = TestData.Line(2, TerrainKind.Road, (120, 0), (120, 300));
        MapFeature coincident = TestData.Line(3, TerrainKind.Road, (100, 0), (100, 300));
        Coordinate from = projection.Forward(TestData.P(50, 150));
        Coordinate to = projection.Forward(TestData.P(250, 150));

        Assert.NotNull(new TerrainEvaluator([railway], projection).Evaluate(from, to));
        Assert.Null(new TerrainEvaluator([railway, road], projection).Evaluate(from, to));
        Assert.Null(new TerrainEvaluator([railway, coincident], projection).Evaluate(from, to));
    }

    [Fact]
    public void RailwayRoadJunctionStillRequiresRoadCrossingEvidence()
    {
        LocalProjection projection = new(TestData.P(0, 0));
        MapFeature railway = TestData.Line(1, Railway, (50, 150), (250, 150));
        MapFeature road = TestData.Line(2, TerrainKind.Road, (150, 50), (150, 250));
        Coordinate from = projection.Forward(TestData.P(50, 150));
        Coordinate to = projection.Forward(TestData.P(250, 150));

        Assert.NotNull(new TerrainEvaluator([railway], projection).Evaluate(from, to));
        Assert.Null(new TerrainEvaluator([railway, road], projection).Evaluate(from, to));
    }

    [Fact]
    public void RailwayAlongRouteIsNeutralWhileAdjacentWaterRemainsBlocked()
    {
        LocalProjection projection = new(TestData.P(0, 0));
        MapFeature railway = TestData.Line(1, Railway, (50, 150), (250, 150));
        MapFeature lake = TestData.Polygon(2, TerrainKind.Lake, 160, 100, 180, 200);
        Coordinate from = projection.Forward(TestData.P(50, 150));
        Coordinate to = projection.Forward(TestData.P(250, 150));

        EdgeEvaluation baseline = Assert.IsType<EdgeEvaluation>(new TerrainEvaluator([], projection).Evaluate(from, to));
        EdgeEvaluation alongTrack = Assert.IsType<EdgeEvaluation>(new TerrainEvaluator([railway], projection).Evaluate(from, to));
        Assert.Equal(baseline.Length, alongTrack.Length);
        Assert.Equal(baseline.Cost(CostProfile.Defaults[0]), alongTrack.Cost(CostProfile.Defaults[0]));
        Assert.Empty(alongTrack.Roads);
        Assert.Null(new TerrainEvaluator([railway, lake], projection).Evaluate(from, to));
    }

    [Theory]
    [InlineData("Railway")]
    [InlineData("RailwayBridge")]
    [InlineData("RailwayTunnel")]
    public void RailwayHasNeutralDomainPolicy(string kind) =>
        Assert.Equal(TerrainTraversalRule.Neutral, TerrainTraversalPolicy.For(Enum.Parse<TerrainKind>(kind)));

    [Fact]
    public async Task LegacySettingsRouteAcrossTracksWithoutChangingFile()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.DirectoryPath, "settings.json");
        string legacy = "{\"CellSizeMetres\":20,\"MaxNodes\":100000}";
        File.WriteAllText(path, legacy);
        RoutingOptions options = new JsonSettingsStore(path).Load();
        MapFeature track = TestData.Line(1, Railway, (150, 0), (150, 300));

        IReadOnlyList<RouteAlternative> routes = await TestData.Planner(track).PlanAsync(
            [TestData.P(50, 150), TestData.P(250, 150)], options, default);

        Assert.NotEmpty(routes);
        Assert.InRange(routes[0].Metrics.LengthMetres, 199, 201);
        Assert.Equal(legacy, File.ReadAllText(path));
    }

    [Fact]
    public async Task TrackDoesNotOpenRoadOrLakeInFullRoute()
    {
        MapFeature track = TestData.Line(1, Railway, (100, -20), (100, 700));
        GeoCoordinate[] endpoints = [TestData.P(50, 150), TestData.P(250, 150)];
        Assert.NotEmpty(await TestData.Planner(track).PlanAsync(endpoints, TestData.Options, default));

        MapFeature road = TestData.Line(2, TerrainKind.Road, (150, -20), (150, 700));
        MapFeature lake = TestData.Polygon(3, TerrainKind.Lake, 149, -20, 151, 700);
        await Assert.ThrowsAsync<PlanningException>(() =>
            TestData.Planner(track, road).PlanAsync(endpoints, TestData.Options, default));
        await Assert.ThrowsAsync<PlanningException>(() =>
            TestData.Planner(track, lake).PlanAsync(endpoints, TestData.Options, default));
    }
}
