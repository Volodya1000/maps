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
}
