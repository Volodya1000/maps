using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using CableRoutePlanner.Infrastructure.Routing;
using CableRoutePlanner.ViewModels;
using NetTopologySuite.Geometries;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class RailwayCrossingTests
{
    [Fact]
    public void ParallelTracksCountSeparatelyIncludingBridgeAndTunnel()
    {
        MapFeature[] tracks = [Track(1, 100), Track(2, 110) with { Kind = TerrainKind.RailwayBridge },
            Track(3, 120) with { Kind = TerrainKind.RailwayTunnel }];
        AssertCount(Build(tracks, [(50, 150), (250, 150)]), 3);
    }

    [Fact]
    public void RepeatVisitsCountEvenAtSameCoordinate()
    {
        AssertCount(Build([Track(1, 150)], [(50, 150), (250, 150), (50, 150)]), 2);
        AssertCount(Build([Track(1, 150)], [(50, 100), (250, 100), (250, 200), (50, 200)]), 2);
    }

    [Fact]
    public void SplitAndReversedOverlappingDuplicatesCountOnceAtRouteVertex()
    {
        MapFeature[] tracks =
        [
            TestData.Line(1, TerrainKind.Railway, (150, 0), (150, 150)),
            TestData.Line(2, TerrainKind.Railway, (150, 150), (150, 300)),
            TestData.Line(3, TerrainKind.Railway, (150, 250), (150, 50)),
            Track(4, 150)
        ];
        AssertCount(Build(tracks, [(50, 150), (150, 150), (250, 150)]), 1);
    }

    [Theory]
    [InlineData(50, 150, 50, 200)] // touches and turns back
    [InlineData(150, 100, 150, 200)] // travels along
    [InlineData(50, 150, 150, 150)] // finishes on track
    [InlineData(150, 150, 250, 150)] // starts on track
    public void TouchSharedSectionAndEndpointsAreNotCrossings(double x1, double y1, double x2, double y2)
    {
        AssertCount(Build([Track(1, 150)], [(x1, y1), (150, 150), (x2, y2)]), 0);
    }

    [Fact]
    public void RailwayEndpointTouchDoesNotCount()
    {
        AssertCount(Build([TestData.Line(1, TerrainKind.Railway, (150, 150), (150, 300))],
            [(50, 150), (250, 150)]), 0);
    }

    [Fact]
    public void BentTrackTangencyDoesNotCountButPassageDoes()
    {
        MapFeature bent = TestData.Line(1, TerrainKind.Railway, (100, 200), (150, 150), (200, 200));
        AssertCount(Build([bent], [(50, 150), (250, 150)]), 0);
        AssertCount(Build([bent], [(150, 100), (150, 250)]), 1);
    }

    [Fact]
    public void AlternativesHaveIndependentCounts()
    {
        LocalProjection projection = new(TestData.P(0, 0));
        TerrainEvaluator evaluator = new([Track(1, 150)], projection);
        RouteAlternativeBuilder builder = new(evaluator, new(evaluator, evaluator.Terrain), evaluator.Terrain, TestData.Options);
        Assert.Null(builder.Add(Project([(50, 150), (250, 150)], projection), CostProfile.Defaults[0], default));
        Assert.Null(builder.Add(Project([(50, 400), (250, 400)], projection), CostProfile.Defaults[1], default));
        IReadOnlyList<RouteAlternative> routes = builder.Complete();
        Assert.Equal(2, routes.Count);
        AssertCount(routes[0], 1);
        AssertCount(routes[1], 0);
    }

    [Fact]
    public async Task PlannerRecalculationPublishesNewCount()
    {
        IRoutePlanner planner = TestData.Planner(Track(1, 150), Track(2, 160));
        IReadOnlyList<RouteAlternative> first = await planner.PlanAsync([TestData.P(50, 150), TestData.P(250, 150)], TestData.Options, default);
        Assert.All(first, route => AssertCount(route, 2));
        IReadOnlyList<RouteAlternative> next = await planner.PlanAsync([TestData.P(50, 400), TestData.P(250, 400)], TestData.Options, default);
        Assert.All(next, route => AssertCount(route, 0));
    }

    private static MapFeature Track(long id, double x) => TestData.Line(id, TerrainKind.Railway, (x, 0), (x, 300));

    private static Coordinate[] Project((double X, double Y)[] route, LocalProjection projection) =>
        route.Select(point => projection.Forward(TestData.P(point.X, point.Y))).ToArray();

    private static RouteAlternative Build(MapFeature[] tracks, (double X, double Y)[] route)
    {
        LocalProjection projection = new(TestData.P(0, 0));
        TerrainEvaluator evaluator = new(tracks, projection);
        RouteAlternativeBuilder builder = new(evaluator, new(evaluator, evaluator.Terrain), evaluator.Terrain, TestData.Options);
        Assert.Null(builder.Add(Project(route, projection), CostProfile.Defaults[0], default));
        return Assert.Single(builder.Complete());
    }

    private static void AssertCount(RouteAlternative route, int expected)
    {
        Assert.Equal(expected, route.Metrics.RailwayCrossings);
        Assert.Contains($"Пересечений железнодорожных путей: {expected}",
            RouteMetricsFormatter.Format(route, TestData.Options), StringComparison.Ordinal);
    }
}
