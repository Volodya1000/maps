using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using CableRoutePlanner.Infrastructure.Routing;
using NetTopologySuite.Geometries;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class RoadContactTests
{
    [Fact]
    public void CancelledContactReviewDoesNotPublishPartialContacts()
    {
        LocalProjection projection = new(TestData.P(0, 0));
        var analyzer = new RoadContactAnalyzer([TestData.Line(1, TerrainKind.Road, (50, -20), (50, 20))], projection);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => analyzer.Analyze(
            [new Coordinate(0, 0), new Coordinate(100, 0)], cancellation.Token));
    }

    [Fact]
    public void DistinguishesPointCrossingFromSharedRoadSectionAndKeepsBothForSameRoad()
    {
        MapFeature road = TestData.Line(12, TerrainKind.Road, (0, 0), (100, 0), (100, 100), (200, 100));
        LocalProjection projection = new(TestData.P(0, 0));
        var analyzer = new RoadContactAnalyzer([road], projection);
        GeoCoordinate[] route = [TestData.P(20, 0), TestData.P(80, 0), TestData.P(80, 50), TestData.P(150, 50)];
        IReadOnlyList<RouteRoadContact> contacts = analyzer.Analyze(route.Select(projection.Forward).ToArray());
        RouteRoadContact shared = Assert.Single(contacts, contact => contact.Kind == RoadContactKind.SharedSection);
        RouteRoadContact crossing = Assert.Single(contacts, contact => contact.Kind == RoadContactKind.Crossing);
        Assert.Equal(12, shared.RoadId);
        Assert.Equal(12, crossing.RoadId);
        Assert.True(shared.Geometry[0].DistanceTo(route[0]) < 0.01);
        Assert.True(shared.Geometry[^1].DistanceTo(route[1]) < 0.01);
        Assert.True(Assert.Single(crossing.Geometry).DistanceTo(TestData.P(100, 50)) < 0.01);
    }

    [Fact]
    public void NearbyParallelRoadAndNonRoadTerrainDoNotCreateContacts()
    {
        LocalProjection projection = new(TestData.P(0, 0));
        var analyzer = new RoadContactAnalyzer([
            TestData.Line(1, TerrainKind.Road, (0, 10), (100, 10)),
            TestData.Line(2, TerrainKind.Clearing, (50, -20), (50, 20))], projection);
        Assert.Empty(analyzer.Analyze([projection.Forward(TestData.P(0, 0)), projection.Forward(TestData.P(100, 0))]));
        Assert.Empty(analyzer.Analyze([]));
    }

    [Fact]
    public async Task PlannerPublishesContactsForFinalGeometryIncludingBridgeAxis()
    {
        MapFeature road = TestData.Line(1, TerrainKind.Road, (100, -100), (100, 500));
        MapFeature bridge = TestData.Line(2, TerrainKind.BridgeCandidate, (80, 150), (120, 150));
        IReadOnlyList<RouteAlternative> routes = await TestData.Planner(road, bridge).PlanAsync(
            [TestData.P(20, 150), TestData.P(280, 150)], TestData.Options, CancellationToken.None);
        RouteAlternative shortest = Assert.Single(routes, route => route.Name == "Короче");
        RouteRoadContact contact = Assert.Single(shortest.RoadContacts!, item => item.RoadId == 1);
        Assert.Equal(RoadContactKind.Crossing, contact.Kind);
        Assert.True(Assert.Single(contact.Geometry).DistanceTo(TestData.P(100, 150)) < 0.01);

        LocalProjection projection = new(TestData.P(0, 0));
        MapFeature bridgeAxis = TestData.Line(2, TerrainKind.BridgeCandidate, (80, 0), (120, 0));
        var analyzer = new RoadContactAnalyzer([bridgeAxis], projection);
        RouteRoadContact shared = Assert.Single(analyzer.Analyze(
            [projection.Forward(TestData.P(80, 0)), projection.Forward(TestData.P(120, 0))]));
        Assert.Equal(RoadContactKind.SharedSection, shared.Kind);
    }
}
