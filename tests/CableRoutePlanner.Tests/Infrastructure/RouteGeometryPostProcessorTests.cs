using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using CableRoutePlanner.Infrastructure.Routing;
using NetTopologySuite.Geometries;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class RouteGeometryPostProcessorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SimplificationPreservesEndpointsAndTheSelectedProfileCost(int profileIndex)
    {
        LocalProjection projection = new(TestData.P(0, 0));
        TerrainEvaluator evaluator = new([TestData.Polygon(1, TerrainKind.Forest, 10, -10, 40, 10)], projection);
        RouteGeometryPostProcessor processor = new(evaluator, evaluator.Terrain);
        Coordinate[] detour = [At(0, 0), At(0, 50), At(50, 50), At(50, 0)];
        CostProfile profile = CostProfile.Defaults[profileIndex];

        List<Coordinate> simplified = processor.SimplifyPath(detour, profile);

        Assert.Equal(detour[0], simplified[0]);
        Assert.Equal(detour[^1], simplified[^1]);
        double cost = simplified.Zip(simplified.Skip(1), (from, to) =>
            Assert.IsType<EdgeEvaluation>(evaluator.Evaluate(from, to)).Cost(profile)).Sum();
        Assert.InRange(cost, 0, 150.01);
        if (profileIndex == 2)
        {
            Assert.True(simplified.Count > 2);
            Assert.True(evaluator.Evaluate(detour[0], detour[^1])!.Cost(profile) > 250);
        }

        Coordinate At(double x, double y) => projection.Forward(TestData.P(x, y));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void EmptyAndCoincidentPathsRetainTheirEndpointMeaning(int count)
    {
        LocalProjection projection = new(TestData.P(0, 0));
        TerrainEvaluator evaluator = new([], projection);
        RouteGeometryPostProcessor processor = new(evaluator, evaluator.Terrain);
        Coordinate point = new(10, 10);
        Coordinate[] path = Enumerable.Repeat(point, count).ToArray();

        List<Coordinate> result = processor.AlignBridgeCrossings(
            processor.SimplifyPath(path, CostProfile.Defaults[0]), CostProfile.Defaults[0]);

        Assert.Empty(processor.BridgeTransitionPoints(result));
        if (count == 0)
        { Assert.Empty(result); }
        else
        {
            Assert.Equal(point, result[0]);
            Assert.Equal(point, result[^1]);
            for (int index = 1; index < result.Count; index++)
            {
                EdgeEvaluation edge = Assert.IsType<EdgeEvaluation>(evaluator.Evaluate(result[index - 1], result[index]));
                Assert.Equal(0, edge.Length);
            }
        }
    }
}
