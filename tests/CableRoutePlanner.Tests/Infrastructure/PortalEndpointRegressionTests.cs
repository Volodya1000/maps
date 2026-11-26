using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using CableRoutePlanner.Infrastructure.Routing;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class PortalEndpointRegressionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReachableDeadEndPortalDoesNotHideAnotherValidBridge(bool workingBridgeFirst, bool reverse)
    {
        MapFeature bridge = NarrowBridgeFeatures()[3] with { Id = workingBridgeFirst ? 4 : 5 };
        MapFeature deadEnd = TestData.Line(workingBridgeFirst ? 5 : 4, TerrainKind.BridgeCandidate,
            (163, 120), (150, 190));
        MapFeature[] features = [.. NarrowBridgeFeatures().Take(3), bridge, deadEnd];
        LocalProjection projection = new(TestData.P(0, 0));
        TerrainEvaluator evaluator = new(features, projection);
        GeoCoordinate[] points = [TestData.P(163, 100), TestData.P(280, 220)];
        Assert.NotNull(evaluator.Evaluate(projection.Forward(points[0]), projection.Forward(TestData.P(163, 120))));
        Assert.Null(evaluator.Evaluate(projection.Forward(TestData.P(163, 120)), projection.Forward(TestData.P(150, 190))));
        if (reverse)
        {
            Array.Reverse(points);
        }

        IReadOnlyList<RouteAlternative> routes = await TestData.Planner(features).PlanAsync(points, TestData.Options, default);

        Assert.NotEmpty(routes);
        Assert.All(routes, route =>
        {
            Assert.Equal(points[0], route.Geometry[0]);
            Assert.Equal(points[1], route.Geometry[^1]);
            Assert.True(route.Metrics.PotentialBridgeCrossings > 0);
            Assert.Equal(0, route.Metrics.ApprovedWaterCrossings);
            for (int index = 1; index < route.Geometry.Count; index++)
            {
                Assert.NotNull(evaluator.Evaluate(projection.Forward(route.Geometry[index - 1]),
                    projection.Forward(route.Geometry[index])));
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndpointInNarrowCorridorCanReachCurvedBridgeWithoutAnIntermediateGridNode(bool reverse)
    {
        MapFeature[] features = NarrowBridgeFeatures();
        GeoCoordinate[] witness =
        [
            TestData.P(163, 100), TestData.P(163, 130), TestData.P(163, 150),
            TestData.P(190, 170), TestData.P(190, 190), TestData.P(280, 220)
        ];
        if (reverse)
        {
            Array.Reverse(witness);
        }
        LocalProjection projection = new(TestData.P(0, 0));
        TerrainEvaluator evaluator = new(features, projection);
        WKBReader reader = new();
        LineString witnessLine = new(witness.Select(point => new Coordinate(point.Longitude, point.Latitude)).ToArray());
        Assert.All(features.Where(feature => feature.Kind == TerrainKind.Building),
            building => Assert.False(witnessLine.Intersects(reader.Read(building.Wkb))));
        for (int index = 1; index < witness.Length; index++)
        {
            Assert.NotNull(evaluator.Evaluate(projection.Forward(witness[index - 1]), projection.Forward(witness[index])));
        }

        IReadOnlyList<RouteAlternative> routes = await TestData.Planner(features).PlanAsync(
            [witness[0], witness[^1]], TestData.Options, default);

        Assert.NotEmpty(routes);
        Assert.All(routes, route =>
        {
            Assert.Equal(witness[0], route.Geometry[0]);
            Assert.Equal(witness[^1], route.Geometry[^1]);
            Assert.True(route.Metrics.PotentialBridgeCrossings > 0);
            Assert.Equal(0, route.Metrics.ApprovedWaterCrossings);
            for (int index = 1; index < route.Geometry.Count; index++)
            {
                Assert.NotNull(evaluator.Evaluate(projection.Forward(route.Geometry[index - 1]),
                    projection.Forward(route.Geometry[index])));
            }
        });
    }

    [Theory]
    [InlineData(TerrainKind.Building)]
    [InlineData(TerrainKind.Road)]
    public async Task PortalConnectorCannotCrossAnUnpermittedBarrier(TerrainKind barrierKind)
    {
        MapFeature barrier = barrierKind == TerrainKind.Building
            ? TestData.Polygon(5, barrierKind, 160, 112, 166, 118)
            : TestData.Line(5, barrierKind, (-500, 115), (1000, 115));
        IRoutePlanner planner = TestData.Planner([.. NarrowBridgeFeatures(), barrier]);

        PlanningException error = await Assert.ThrowsAsync<PlanningException>(() => planner.PlanAsync(
            [TestData.P(163, 100), TestData.P(280, 220)], TestData.Options, default));

        Assert.Equal(PlanningErrorReason.GridConnectionUnavailable, error.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NarrowCorridorEndpointCanEnterOnlyAConfirmedUnderpassEntrance(bool entrance)
    {
        MapFeature tunnel = TestData.Line(4, TerrainKind.PedestrianUnderpassCandidate,
            (163, 130), (163, 150), (190, 170), (190, 190)) with
        {
            UnderpassRoadIds = new long[] { 3 },
            UnderpassTopology = new(4, [10, 11, 12, 13], entrance ? [10, 13] : [13])
        };
        MapFeature[] features =
        [
            .. NarrowBridgeFeatures().Take(2),
            TestData.Line(3, TerrainKind.Road, (-500, 150), (1000, 150)),
            tunnel
        ];
        GeoCoordinate[] points = [TestData.P(163, 100), TestData.P(280, 220)];
        RoutingOptions options = TestData.Options with { AllowExperimentalPedestrianUnderpasses = true };
        IRoutePlanner planner = TestData.Planner(features);
        if (!entrance)
        {
            PlanningException error = await Assert.ThrowsAsync<PlanningException>(() => planner.PlanAsync(points, options, default));
            Assert.Equal(PlanningErrorReason.GridConnectionUnavailable, error.Reason);
            return;
        }

        IReadOnlyList<RouteAlternative> routes = await planner.PlanAsync(points, options, default);

        Assert.NotEmpty(routes);
        LocalProjection projection = new(TestData.P(0, 0));
        TerrainEvaluator evaluator = new(features, projection, allowUnderpasses: true);
        Assert.All(routes, route =>
        {
            Assert.Equal(points[0], route.Geometry[0]);
            Assert.Equal(points[1], route.Geometry[^1]);
            Assert.Single(route.UnderpassPassages!);
            Assert.Contains(4L, route.UnderpassCandidateIds!);
            for (int index = 1; index < route.Geometry.Count; index++)
            {
                Assert.NotNull(evaluator.Evaluate(projection.Forward(route.Geometry[index - 1]),
                    projection.Forward(route.Geometry[index])));
            }
        });
    }

    private static MapFeature[] NarrowBridgeFeatures() =>
    [
        TestData.Polygon(1, TerrainKind.Building, -500, 0, 161, 139),
        TestData.Polygon(2, TerrainKind.Building, 165, 0, 1000, 139),
        TestData.Polygon(3, TerrainKind.River, -500, 140, 1000, 180),
        TestData.Line(4, TerrainKind.BridgeCandidate, (163, 130), (163, 150), (190, 170), (190, 190))
    ];
}
