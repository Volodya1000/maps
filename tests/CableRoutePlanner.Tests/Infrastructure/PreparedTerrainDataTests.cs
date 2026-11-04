using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using CableRoutePlanner.Infrastructure.Routing;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class PreparedTerrainDataTests
{
    private static readonly double[] PortalOrder = [5, 20, 30, 10];

    [Fact]
    public void PublishedSnapshotDoesNotReadSourceWkbDuringSegmentEvaluation()
    {
        LocalProjection projection = new(TestData.P(0, 0));
        MapFeature[] features =
        [
            TestData.Line(1, TerrainKind.Road, (100, -50), (100, 50)),
            TestData.Line(2, TerrainKind.BridgeCandidate, (90, 0), (110, 0)),
            TestData.Line(3, TerrainKind.GasPipelineCrossingCandidate, (90, 0), (110, 0))
        ];
        PreparedTerrainData terrain = new(features, projection);
        foreach (MapFeature feature in features)
        { Array.Clear(feature.Wkb); }
        TerrainEvaluator evaluator = new(terrain);

        EdgeEvaluation edge = Assert.IsType<EdgeEvaluation>(evaluator.EvaluateForSearch(
            projection.Forward(TestData.P(50, 0)), projection.Forward(TestData.P(150, 0))));

        Assert.Equal(1, Assert.Single(edge.Roads));
        Assert.Equal(2, Assert.Single(edge.BridgeCandidates));
        Assert.Equal(3, Assert.Single(edge.GasCandidates));
        Assert.Equal(1, edge.GasRoadCrossings);
    }

    [Fact]
    public void OneProjectionPassPreservesOriginalGasPortalOrder()
    {
        LocalProjection projection = new(TestData.P(0, 0));
        PreparedTerrainData terrain = new(
            [TestData.Line(30, TerrainKind.GasPipelineCrossingCandidate, (30, 1), (30, 3)),
                TestData.Line(20, TerrainKind.BridgeCandidate, (20, 1), (20, 3)),
                TestData.Line(10, TerrainKind.GasPipelineCrossingCandidate, (10, 1), (10, 3)),
                TestData.Line(5, TerrainKind.BridgeCandidate, (5, 1), (5, 3))], projection);

        double[] positions = terrain.TransitionPortals.Select(portal => Math.Round(portal.Coordinates[0].X, 5)).ToArray();

        // Bridges retain sorted preparation order, gas retains repository order.
        Assert.Equal(PortalOrder, positions);
    }
}
