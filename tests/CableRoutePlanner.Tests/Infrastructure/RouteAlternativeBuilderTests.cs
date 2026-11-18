using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using CableRoutePlanner.Infrastructure.Routing;
using NetTopologySuite.Geometries;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class RouteAlternativeBuilderTests
{
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
