using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using CableRoutePlanner.Infrastructure.Routing;
using NetTopologySuite.Geometries;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class GridEdgeEvaluationCacheTests
{
    private static readonly LocalProjection Projection = new(TestData.P(0, 0));
    private static readonly GridMesh Mesh = new(
        [Projection.Forward(TestData.P(0, 0)), Projection.Forward(TestData.P(10, 0))],
        2, 1, new Coordinate(0, 0), 10);

    [Fact]
    public void RepeatedAndReverseAccessReuseTheSameMeasuredFacts()
    {
        GridEdgeEvaluationCache cache = new(Mesh, new TerrainEvaluator([], Projection));
        Assert.Equal(0, cache.EvaluationCount);

        EdgeEvaluation first = Assert.IsType<EdgeEvaluation>(cache.Get(0, 1));

        Assert.Equal(1, cache.EvaluationCount);
        Assert.InRange(first.Length, 9.999, 10.001);
        Assert.Same(first, cache.Get(0, 1));
        Assert.Same(first, cache.Get(1, 0));
        Assert.Equal(1, cache.EvaluationCount);
    }

    [Fact]
    public void BlockedEdgesAreCachedInBothDirections()
    {
        MapFeature building = TestData.Polygon(1, TerrainKind.Building, 4, -5, 6, 5);
        GridEdgeEvaluationCache cache = new(Mesh, new TerrainEvaluator([building], Projection));

        Assert.Null(cache.Get(0, 1));
        Assert.Null(cache.Get(1, 0));
        Assert.Null(cache.Get(0, 1));

        Assert.Equal(1, cache.EvaluationCount);
    }

    [Fact]
    public void SeparateCalculationsKeepDifferentTerrainAndStatistics()
    {
        GridEdgeEvaluationCache clear = new(Mesh, new TerrainEvaluator([], Projection));
        GridEdgeEvaluationCache blocked = new(Mesh, new TerrainEvaluator(
            [TestData.Polygon(1, TerrainKind.Marsh, 4, -5, 6, 5)], Projection));

        Assert.NotNull(clear.Get(0, 1));
        Assert.Equal(0, blocked.EvaluationCount);
        Assert.Null(blocked.Get(0, 1));
        Assert.NotNull(clear.Get(1, 0));

        Assert.Equal(1, clear.EvaluationCount);
        Assert.Equal(1, blocked.EvaluationCount);
    }

    [Fact]
    public void DifferentProfilesPriceOneSharedSetOfTerrainFacts()
    {
        MapFeature forest = TestData.Polygon(1, TerrainKind.Forest, -1, -1, 11, 1);
        GridEdgeEvaluationCache cache = new(Mesh, new TerrainEvaluator([forest], Projection));
        EdgeEvaluation facts = Assert.IsType<EdgeEvaluation>(cache.Get(0, 1));

        foreach (CostProfile profile in CostProfile.Defaults)
        {
            EdgeEvaluation sameFacts = Assert.IsType<EdgeEvaluation>(cache.Get(1, 0));
            Assert.Same(facts, sameFacts);
            Assert.Equal(facts.Length * (1 + profile.Forest), sameFacts.Cost(profile), 6);
        }
        Assert.Equal(1, cache.EvaluationCount);
    }

    [Fact]
    public async Task ParallelPlannerRequestsDoNotMixCalculationState()
    {
        IRoutePlanner planner = TestData.Planner(TestData.Polygon(1, TerrainKind.Building, 120, 120, 180, 180));
        GeoCoordinate[][] cases = Enumerable.Range(0, 4)
            .Select(index => new[] { TestData.P(50, 100 + index * 20), TestData.P(280, 200 - index * 10) }).ToArray();
        List<IReadOnlyList<RouteAlternative>> expected = [];
        foreach (GeoCoordinate[] points in cases)
        { expected.Add(await planner.PlanAsync(points, TestData.Options, default)); }

        IReadOnlyList<RouteAlternative>[] concurrent = await Task.WhenAll(
            cases.Select(points => planner.PlanAsync(points, TestData.Options, default)));

        for (int index = 0; index < cases.Length; index++)
        {
            Assert.Equal(expected[index].Count, concurrent[index].Count);
            for (int alternative = 0; alternative < expected[index].Count; alternative++)
            {
                Assert.Equal(expected[index][alternative].Geometry, concurrent[index][alternative].Geometry);
                Assert.Equal(expected[index][alternative].Metrics, concurrent[index][alternative].Metrics);
                Assert.Equal(expected[index][alternative].GasPipelineCandidateIds, concurrent[index][alternative].GasPipelineCandidateIds);
            }
        }
    }

    [Fact]
    public async Task ConcurrentBridgeRequestsCanShareOnePreparedSnapshot()
    {
        IRoutePlanner planner = TestData.Planner(
            TestData.Polygon(1, TerrainKind.River, 100, -1000, 180, 1000),
            TestData.Line(2, TerrainKind.BridgeCandidate, (80, 150), (200, 150)),
            TestData.Line(3, TerrainKind.Road, (140, -1000), (140, 1000)));
        GeoCoordinate[] points = [TestData.P(50, 150), TestData.P(250, 150)];
        IReadOnlyList<RouteAlternative> expected = await planner.PlanAsync(points, TestData.Options, default);
        Assert.NotEmpty(expected);
        Assert.All(expected, route => Assert.Equal(1, route.Metrics.PotentialBridgeCrossings));

        IReadOnlyList<RouteAlternative>[] parallel = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => planner.PlanAsync(points, TestData.Options, default)));

        foreach (IReadOnlyList<RouteAlternative> routes in parallel)
        {
            Assert.Equal(expected.Count, routes.Count);
            for (int index = 0; index < routes.Count; index++)
            {
                Assert.Equal(expected[index].Geometry, routes[index].Geometry);
                Assert.Equal(expected[index].Metrics, routes[index].Metrics);
                Assert.Equal(expected[index].TransitionPoints, routes[index].TransitionPoints);
                IReadOnlyList<RouteRoadContact>? originalContacts = expected[index].RoadContacts;
                IReadOnlyList<RouteRoadContact>? concurrentContacts = routes[index].RoadContacts;
                Assert.NotNull(originalContacts);
                Assert.NotNull(concurrentContacts);
                Assert.Equal(originalContacts.Count, concurrentContacts.Count);
                for (int contactIndex = 0; contactIndex < originalContacts.Count; contactIndex++)
                {
                    Assert.Equal(originalContacts[contactIndex].RoadId, concurrentContacts[contactIndex].RoadId);
                    Assert.Equal(originalContacts[contactIndex].Kind, concurrentContacts[contactIndex].Kind);
                    Assert.Equal(originalContacts[contactIndex].Geometry, concurrentContacts[contactIndex].Geometry);
                }
                Assert.Equal(expected[index].GasPipelineCandidateIds, routes[index].GasPipelineCandidateIds);
            }
        }
    }
}
