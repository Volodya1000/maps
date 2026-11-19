using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using CableRoutePlanner.Infrastructure.Routing;
using NetTopologySuite.Geometries;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class RouteAlternativeBuilderTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public void ContinuousUnderpassCountsOnceAcrossTwoRoadsAndCountsReentry(bool reenter, int expected)
    {
        MapFeature first = TestData.Line(1, TerrainKind.Road, (-100, 145), (700, 145));
        MapFeature second = TestData.Line(3, TerrainKind.Road, (-100, 155), (700, 155));
        MapFeature tunnel = TestData.Line(2, TerrainKind.PedestrianUnderpassCandidate, (160, 130), (160, 170))
            with
        { UnderpassRoadIds = new long[] { 1, 3 } };
        LocalProjection projection = new(TestData.P(0, 0));
        TerrainEvaluator evaluator = new([first, second, tunnel], projection, allowUnderpasses: true);
        RouteAlternativeBuilder builder = new(evaluator, new(evaluator, evaluator.Terrain), evaluator.Terrain, TestData.Options);
        List<GeoCoordinate> points = [TestData.P(160, 120), TestData.P(160, 150), TestData.P(160, 180)];
        if (reenter)
        {
            points.Add(TestData.P(160, 150));
            points.Add(TestData.P(160, 120));
        }
        Assert.Null(builder.Add(points.Select(projection.Forward).ToArray(), CostProfile.Defaults[0], default));
        RouteAlternative route = Assert.Single(builder.Complete());
        Assert.Equal(expected, route.UnderpassPassages!.Count);
        Assert.Equal(2, route.Metrics.RoadCrossings);
        Assert.Equal(reenter ? 4 : 2, route.RoadCrossingEvents!.Count);
        Assert.All(route.UnderpassPassages, passage => Assert.Equal(2, passage.NetworkId));
        string text = CableRoutePlanner.ViewModels.RouteMetricsFormatter.Format(route, TestData.Options);
        Assert.Contains($"Через подземный пешеходный переход: {expected}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedOsmNodeUnitesPassageButNearbyUnconnectedWaysRemainSeparate()
    {
        MapFeature firstRoad = TestData.Line(1, TerrainKind.Road, (-100, 140), (700, 140));
        MapFeature secondRoad = TestData.Line(3, TerrainKind.Road, (-100, 160), (700, 160));
        MapFeature first = TestData.Line(2, TerrainKind.PedestrianUnderpassCandidate, (160, 130), (160, 150))
            with
        { UnderpassRoadIds = new long[] { 1 }, UnderpassTopology = new(2, [10, 11], [10]) };
        MapFeature second = TestData.Line(4, TerrainKind.PedestrianUnderpassCandidate, (160, 150), (160, 170))
            with
        { UnderpassRoadIds = new long[] { 3 }, UnderpassTopology = new(2, [11, 12], [12]) };
        foreach (bool connected in new[] { true, false })
        {
            LocalProjection projection = new(TestData.P(0, 0));
            TerrainEvaluator evaluator = new([firstRoad, secondRoad, first, connected ? second : second with { UnderpassTopology = new(4, [20, 21], [20, 21]) }], projection, allowUnderpasses: true);
            RouteAlternativeBuilder builder = new(evaluator, new(evaluator, evaluator.Terrain), evaluator.Terrain, TestData.Options);
            Assert.Null(builder.Add(new[] { TestData.P(160, 120), TestData.P(160, 150), TestData.P(160, 180) }.Select(projection.Forward).ToArray(), CostProfile.Defaults[0], default));
            RouteAlternative route = Assert.Single(builder.Complete());
            Assert.Equal(connected ? 1 : 2, route.UnderpassPassages!.Count);
            Assert.Equal(2, route.RoadCrossingEvents!.Count);
        }
    }

    [Fact]
    public void FinalCountsAreUniqueWhileDomainCostsRemainPerSegment()
    {
        LocalProjection projection = new(TestData.P(0, 0));
        TerrainEvaluator evaluator = new([
            TestData.Line(1, TerrainKind.Road, (150, 0), (150, 300)),
            TestData.Line(2, TerrainKind.BridgeCandidate, (130, 150), (170, 150))], projection);
        RouteAlternativeBuilder builder = new(evaluator, new(evaluator, evaluator.Terrain), evaluator.Terrain, TestData.Options);
        GeoCoordinate[] geographic = [TestData.P(50, 150), TestData.P(150, 150), TestData.P(250, 150)];
        Coordinate[] path = geographic.Select(projection.Forward).ToArray();
        CostProfile profile = CostProfile.Defaults[1];
        Assert.Null(builder.Add(path, profile, default));
        RouteAlternative route = Assert.Single(builder.Complete());
        Assert.Equal(1, route.Metrics.RoadCrossings);
        Assert.Equal(1, route.Metrics.PotentialBridgeCrossings);
        double expected = Enumerable.Range(1, path.Length - 1).Sum(index =>
            RouteCostCalculator.Calculate(new SegmentTerrainFacts(
                projection.Inverse(path[index - 1]).DistanceTo(projection.Inverse(path[index])),
                0, 0, 0, 1, 1, 0), profile));
        Assert.Equal(expected, route.Metrics.WeightedCost, 6);
        Assert.Equal(path.Select(projection.Inverse), route.Geometry);
    }

    [Fact]
    public void DuplicateCorridorsKeepTheFirstProfileAndOriginalGeometry()
    {
        TerrainEvaluator evaluator = new([], new LocalProjection(TestData.P(0, 0)));
        RouteAlternativeBuilder builder = new(evaluator, new(evaluator, evaluator.Terrain), evaluator.Terrain, TestData.Options);
        Coordinate[] path = [new(20, 20), new(200, 200)];
        Assert.Null(builder.Add(path, CostProfile.Defaults[0], default));
        Assert.Null(builder.Add(path, CostProfile.Defaults[1], default));
        RouteAlternative route = Assert.Single(builder.Complete());
        Assert.Equal(CostProfile.Defaults[0].Name, route.Name);
        Assert.Equal(path.Select(evaluator.Terrain.Projection.Inverse), route.Geometry);
    }

    [Fact]
    public void CoincidentUnderpassCandidatesCountAsOneRoadCrossing()
    {
        MapFeature road = TestData.Line(1, TerrainKind.Road, (-100, 150), (700, 150));
        MapFeature first = TestData.Line(2, TerrainKind.PedestrianUnderpassCandidate, (160, 130), (160, 170))
            with
        { UnderpassRoadIds = new long[] { 1 } };
        MapFeature second = first with { Id = 3 };
        LocalProjection projection = new(TestData.P(0, 0));
        TerrainEvaluator evaluator = new([road, first, second], projection, allowUnderpasses: true);
        RouteAlternativeBuilder builder = new(evaluator, new(evaluator, evaluator.Terrain), evaluator.Terrain, TestData.Options);

        Assert.Null(builder.Add([projection.Forward(TestData.P(160, 140)), projection.Forward(TestData.P(160, 160))],
            CostProfile.Defaults[0], default));

        RouteAlternative route = Assert.Single(builder.Complete());
        Assert.Single(route.RoadCrossingEvents!);
        Assert.Equal(new long[] { 2, 3 }, route.UnderpassCandidateIds);
    }

    [Fact]
    public void SeparateCrossingsOfTheSameRoadCountAsSeparateEvents()
    {
        MapFeature road = TestData.Line(1, TerrainKind.Road, (-100, 150), (700, 150));
        MapFeature first = TestData.Line(2, TerrainKind.PedestrianUnderpassCandidate, (160, 130), (160, 170))
            with
        { UnderpassRoadIds = new long[] { 1 } };
        MapFeature second = TestData.Line(3, TerrainKind.PedestrianUnderpassCandidate, (180, 130), (180, 170))
            with
        { UnderpassRoadIds = new long[] { 1 } };
        LocalProjection projection = new(TestData.P(0, 0));
        TerrainEvaluator evaluator = new([road, first, second], projection, allowUnderpasses: true);
        RouteAlternativeBuilder builder = new(evaluator, new(evaluator, evaluator.Terrain), evaluator.Terrain, TestData.Options);

        Assert.Null(builder.Add([
            projection.Forward(TestData.P(160, 140)),
            projection.Forward(TestData.P(160, 160)),
            projection.Forward(TestData.P(180, 160)),
            projection.Forward(TestData.P(180, 140))], CostProfile.Defaults[0], default));

        RouteAlternative route = Assert.Single(builder.Complete());
        Assert.Equal(2, route.RoadCrossingEvents!.Count);
        Assert.All(route.RoadCrossingEvents, crossing => Assert.Equal(1, crossing.RoadId));
    }

    [Fact]
    public void CompletionKeepsSpecificRejectionMeasurementsAndOtherFailures()
    {
        TerrainEvaluator evaluator = new([], new LocalProjection(TestData.P(0, 0)));
        RoutingOptions options = TestData.Options with { MaxRouteLengthMetres = 100 };
        RouteAlternativeBuilder builder = new(evaluator, new(evaluator, evaluator.Terrain), evaluator.Terrain, options);
        PlanningException connection = new("localized independently", PlanningErrorReason.GridConnectionUnavailable);
        builder.RecordFailure(CostProfile.Defaults[0], connection);
        PlanningException rejection = Assert.IsType<PlanningException>(builder.Add(
            [new(20, 20), new(200, 20)], CostProfile.Defaults[1], default));
        builder.RecordFailure(CostProfile.Defaults[1], rejection);
        PlanningException error = Assert.Throws<PlanningException>(() => builder.Complete());
        Assert.Equal(PlanningErrorReason.RouteLengthLimitExceeded, error.Reason);
        Assert.Same(rejection, error.InnerException);
        Assert.Equal(rejection.ActualRouteLengthMetres, error.ActualRouteLengthMetres);
        Assert.Equal(100, error.MaximumRouteLengthMetres);
        Assert.Collection(error.SearchFailures,
            failure => Assert.Same(connection, failure.Error),
            failure => Assert.Same(rejection, failure.Error));
    }
}
