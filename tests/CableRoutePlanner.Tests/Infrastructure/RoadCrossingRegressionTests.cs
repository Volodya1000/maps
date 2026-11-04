using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using CableRoutePlanner.Infrastructure.Routing;
using NetTopologySuite.Geometries;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class RoadCrossingRegressionTests
{
    private static readonly LocalProjection Projection = new(TestData.P(0, 0));

    [Fact]
    public void MixedIntersectionCannotHideAnUnmappedPointCrossing()
    {
        TerrainEvaluator evaluator = new([MixedRoad()], Projection);

        Assert.Null(evaluator.Evaluate(At(50, 0), At(300, 0)));
        Assert.Null(evaluator.EvaluateForSearch(At(50, 0), At(300, 0)));
    }

    [Theory]
    [InlineData(TerrainKind.BridgeCandidate)]
    [InlineData(TerrainKind.GasPipelineCrossingCandidate)]
    public void MixedIntersectionCountsThePermittedPointCrossing(TerrainKind kind)
    {
        TerrainEvaluator evaluator = new([MixedRoad(), Candidate(2, kind, 100)], Projection);

        EdgeEvaluation edge = Assert.IsType<EdgeEvaluation>(evaluator.Evaluate(At(50, 0), At(300, 0)));

        Assert.Equal(new long[] { 1 }, edge.Roads);
        Assert.Equal(kind == TerrainKind.BridgeCandidate ? new long[] { 2 } : [], edge.BridgeCandidates);
        Assert.Equal(kind == TerrainKind.GasPipelineCrossingCandidate ? new long[] { 2 } : [], edge.GasCandidates);
    }

    [Theory]
    [InlineData(TerrainKind.BridgeCandidate, false)]
    [InlineData(TerrainKind.BridgeCandidate, true)]
    [InlineData(TerrainKind.GasPipelineCrossingCandidate, false)]
    [InlineData(TerrainKind.GasPipelineCrossingCandidate, true)]
    public void EveryPointCrossingRequiresItsOwnCandidate(TerrainKind kind, bool coverBoth)
    {
        MapFeature road = TestData.Line(1, TerrainKind.Road,
            (100, -50), (100, 50), (200, 50), (200, -50));
        List<MapFeature> features = [road, Candidate(2, kind, 100)];
        if (coverBoth)
        {
            features.Add(Candidate(3, kind, 200));
        }
        TerrainEvaluator evaluator = new(features, Projection);

        EdgeEvaluation? edge = evaluator.Evaluate(At(50, 0), At(300, 0));

        if (!coverBoth)
        {
            Assert.Null(edge);
            return;
        }
        Assert.NotNull(edge);
        Assert.Equal(new long[] { 1 }, edge.Roads);
        Assert.Equal(new long[] { 2, 3 }, kind == TerrainKind.BridgeCandidate ? edge.BridgeCandidates : edge.GasCandidates);
    }

    [Fact]
    public void NearbyGasCrossingsKeepIndependentReviewWindows()
    {
        MapFeature road = TestData.Line(1, TerrainKind.Road,
            (100, -50), (100, 50), (105, 50), (105, -50));
        TerrainEvaluator evaluator = new(
            [road,
                TestData.Line(2, TerrainKind.GasPipelineCrossingCandidate, (96, 0), (104, 0)),
                TestData.Line(3, TerrainKind.GasPipelineCrossingCandidate, (101, 0), (109, 0))], Projection);

        Assert.NotNull(evaluator.Evaluate(At(50, 0), At(103, 0)));
        Assert.NotNull(evaluator.Evaluate(At(102, 0), At(150, 0)));
        EdgeEvaluation edge = Assert.IsType<EdgeEvaluation>(evaluator.Evaluate(At(50, 0), At(150, 0)));
        Assert.Equal(new long[] { 2, 3 }, edge.GasCandidates);
        Assert.Equal(new long[] { 1 }, edge.Roads);
    }

    [Fact]
    public void GasReviewWindowDoesNotIncludeAnAdjacentSharedRoadSection()
    {
        MapFeature road = TestData.Line(1, TerrainKind.Road,
            (100, -50), (100, 50), (105, 50), (105, 0), (130, 0));
        TerrainEvaluator evaluator = new(
            [road, TestData.Line(2, TerrainKind.GasPipelineCrossingCandidate, (96, 0), (104, 0))], Projection);

        EdgeEvaluation edge = Assert.IsType<EdgeEvaluation>(evaluator.Evaluate(At(50, 0), At(150, 0)));

        Assert.Equal(new long[] { 2 }, edge.GasCandidates);
        Assert.Equal(new long[] { 1 }, edge.Roads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObliqueGasCrossingRequiresAlignmentAcrossTheFullRoadReviewWindow(bool aligned)
    {
        MapFeature road = TestData.Line(1, TerrainKind.Road, (100, -100), (100, 100));
        // At this angle a 3 m road buffer covers much more than 3 m of route.
        MapFeature gas = aligned
            ? TestData.Line(2, TerrainKind.GasPipelineCrossingCandidate, (90, -100), (110, 100))
            : TestData.Line(2, TerrainKind.GasPipelineCrossingCandidate, (90, -50), (110, 50));
        TerrainEvaluator evaluator = new([road, gas], Projection);

        EdgeEvaluation? edge = evaluator.Evaluate(At(95, -50), At(105, 50));

        if (aligned)
        {
            Assert.NotNull(edge);
            Assert.Equal(new long[] { 2 }, edge.GasCandidates);
        }
        else
        {
            Assert.Null(edge);
        }
    }

    [Fact]
    public void UnmappedPointIsBlockedWhileSharedRoadSectionRemainsTraversable()
    {
        TerrainEvaluator evaluator = new([TestData.Line(1, TerrainKind.Road, (100, -50), (100, 50))], Projection);
        Assert.Null(evaluator.Evaluate(At(50, 0), At(150, 0)));
        Assert.NotNull(evaluator.Evaluate(At(100, -20), At(100, 20)));
        // An isolated contact at an edge endpoint must remain checked: two such
        // edges must not cross a road by introducing a vertex on its axis.
        Assert.Null(evaluator.Evaluate(At(50, 0), At(100, 0)));
    }

    [Fact]
    public void SimplificationCannotIntroduceAnUnmappedMixedCrossing()
    {
        TerrainEvaluator evaluator = new([MixedRoad()], Projection);
        Coordinate[] detour = [At(50, 0), At(50, -80), At(300, -80), At(300, 0)];

        RouteGeometryPostProcessor postProcessor = new(evaluator, evaluator.Terrain);
        List<Coordinate> simplified = postProcessor.SimplifyPath(detour, CostProfile.Defaults[0]);

        Assert.True(simplified.Count > 2);
        for (int index = 1; index < simplified.Count; index++)
        {
            Assert.NotNull(evaluator.Evaluate(simplified[index - 1], simplified[index]));
        }
    }

    [Fact]
    public async Task ShortestDirectShortcutCannotIntroduceAnUnmappedMixedCrossing()
    {
        IReadOnlyList<RouteAlternative> routes = await TestData.Planner(MixedRoad()).PlanAsync(
            [TestData.P(50, 0), TestData.P(300, 0)], TestData.Options, default);

        Assert.All(routes, route => Assert.True(route.Metrics.LengthMetres > 251));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BridgeUsedForWaterAndRoadIsChargedOncePerEdge(bool preferGas)
    {
        MapFeature[] features =
        [
            TestData.Polygon(10, TerrainKind.River, 130, -50, 170, 50),
            TestData.Line(1, TerrainKind.Road, (150, -100), (150, 100)),
            TestData.Line(2, TerrainKind.BridgeCandidate, (110, 0), (190, 0))
        ];
        TerrainEvaluator evaluator = new(features, Projection, preferGas: preferGas);
        EdgeEvaluation edge = Assert.IsType<EdgeEvaluation>(evaluator.Evaluate(At(100, 0), At(200, 0)));

        Assert.Equal(new long[] { 2 }, edge.BridgeCandidates);
        Assert.Empty(edge.GasCandidates);
        Assert.Equal(0, edge.GasRoadCrossings);
        foreach (CostProfile profile in CostProfile.Defaults)
        {
            Assert.Equal(edge.Length + 2 * profile.RoadCrossingMetres, edge.Cost(profile), 6);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void GasPreferenceDiscountRequiresAnApplicableGasCandidate(bool withGas, bool preferGas)
    {
        List<MapFeature> features =
        [TestData.Line(1, TerrainKind.Road, (100, -50), (100, 50)), Candidate(2, TerrainKind.BridgeCandidate, 100)];
        if (withGas)
        {
            features.Add(Candidate(3, TerrainKind.GasPipelineCrossingCandidate, 100));
        }
        TerrainEvaluator evaluator = new(features, Projection, preferGas: preferGas);
        EdgeEvaluation edge = Assert.IsType<EdgeEvaluation>(evaluator.Evaluate(At(50, 0), At(150, 0)));

        Assert.Equal(withGas ? new long[] { 3 } : [], edge.GasCandidates);
        Assert.Equal(withGas ? 1 : 0, edge.GasRoadCrossings);
        foreach (CostProfile profile in CostProfile.Defaults)
        {
            Assert.Equal(edge.Length + (withGas && preferGas ? 1 : 2) * profile.RoadCrossingMetres, edge.Cost(profile), 6);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GasOnlyCrossingUsesTheSameCostRuleForEveryProfile(bool preferGas)
    {
        TerrainEvaluator evaluator = new(
            [TestData.Line(1, TerrainKind.Road, (100, -50), (100, 50)),
                Candidate(2, TerrainKind.GasPipelineCrossingCandidate, 100)], Projection, preferGas: preferGas);

        EdgeEvaluation edge = Assert.IsType<EdgeEvaluation>(evaluator.Evaluate(At(50, 0), At(150, 0)));

        Assert.Empty(edge.BridgeCandidates);
        Assert.Equal(new long[] { 2 }, edge.GasCandidates);
        Assert.Equal(1, edge.GasRoadCrossings);
        foreach (CostProfile profile in CostProfile.Defaults)
        {
            Assert.Equal(edge.Length + (preferGas ? 0 : 1) * profile.RoadCrossingMetres, edge.Cost(profile), 6);
        }
    }

    [Fact]
    public void CandidateIdentifiersAndCostsDoNotDependOnFeatureOrder()
    {
        MapFeature[] features =
        [
            TestData.Line(1, TerrainKind.Road, (100, -50), (100, 50), (200, 50), (200, -50)),
            Candidate(3, TerrainKind.BridgeCandidate, 200),
            Candidate(2, TerrainKind.GasPipelineCrossingCandidate, 100),
            Candidate(4, TerrainKind.BridgeCandidate, 100)
        ];
        TerrainEvaluator forward = new(features, Projection, preferGas: true);
        TerrainEvaluator reverse = new(features.Reverse().ToArray(), Projection, preferGas: true);

        EdgeEvaluation first = Assert.IsType<EdgeEvaluation>(forward.Evaluate(At(50, 0), At(300, 0)));
        EdgeEvaluation second = Assert.IsType<EdgeEvaluation>(reverse.Evaluate(At(50, 0), At(300, 0)));

        Assert.Equal(new long[] { 3, 4 }, first.BridgeCandidates);
        Assert.Equal(new long[] { 2 }, first.GasCandidates);
        Assert.Equal(first.BridgeCandidates, second.BridgeCandidates);
        Assert.Equal(first.GasCandidates, second.GasCandidates);
        foreach (CostProfile profile in CostProfile.Defaults)
        {
            Assert.Equal(first.Length + 2 * profile.RoadCrossingMetres, first.Cost(profile), 6);
            Assert.Equal(first.Cost(profile), second.Cost(profile));
        }
    }

    [Fact]
    public async Task BridgeOnlyRouteMetricsAndCostAreUnaffectedByGasPreference()
    {
        MapFeature[] features =
        [
            TestData.Polygon(10, TerrainKind.River, 130, -50, 170, 500),
            TestData.Line(1, TerrainKind.Road, (150, -5000), (150, 5000)),
            TestData.Line(2, TerrainKind.BridgeCandidate, (110, 150), (190, 150))
        ];
        GeoCoordinate[] points = [TestData.P(110, 150), TestData.P(190, 150)];
        IReadOnlyList<RouteAlternative> ordinary = await TestData.Planner(features).PlanAsync(points, TestData.Options, default);
        IReadOnlyList<RouteAlternative> preferred = await TestData.Planner(features).PlanAsync(
            points, TestData.Options with { PreferOverheadGasCrossings = true }, default);

        Assert.Equal(ordinary.Select(route => route.Metrics), preferred.Select(route => route.Metrics));
        Assert.All(preferred, route =>
        {
            Assert.Equal(1, route.Metrics.PotentialBridgeCrossings);
            Assert.Equal(1, route.Metrics.RoadCrossings);
            Assert.Equal(0, route.Metrics.ApprovedWaterCrossings);
            Assert.Equal(0, route.Metrics.PotentialGasPipelineCrossings);
            Assert.Empty(route.GasPipelineCandidateIds!);
        });
    }

    private static MapFeature MixedRoad() => TestData.Line(1, TerrainKind.Road,
        (100, -50), (100, 50), (200, 50), (200, 0), (250, 0));

    private static MapFeature Candidate(long id, TerrainKind kind, double x) =>
        TestData.Line(id, kind, (x - 10, 0), (x + 10, 0));

    private static Coordinate At(double x, double y) => Projection.Forward(TestData.P(x, y));
}
