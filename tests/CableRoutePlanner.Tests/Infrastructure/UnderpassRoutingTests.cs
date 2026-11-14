using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using CableRoutePlanner.Infrastructure.Routing;
using NetTopologySuite.Geometries;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class UnderpassRoutingTests
{
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
        Assert.Contains(routes, route => route.RoadContacts!.Any(contact => contact.Geometry.Any(point => point.DistanceTo(TestData.P(163, 150)) < 2)));
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
