using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Routing;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class RecoveredBoundsTests
{
    [Fact]
    public async Task RecoveredBridgeAlignmentStaysInsideLoadedAreaAndAvoidsUnloadedBuilding()
    {
        MapFeature river = TestData.Polygon(1, TerrainKind.River, -5, -20_000, 5, 20_000);
        MapFeature bridge = TestData.Line(2, TerrainKind.BridgeCandidate, (-2_000, 12.5), (2_000, 12.5));
        MapFeature building = TestData.Polygon(3, TerrainKind.Building, -1_820, -10, -1_780, 40);
        GeoCoordinate southwest = TestData.P(-6_000, -6_000);
        GeoCoordinate northeast = TestData.P(6_000, 6_000);
        GeoBounds packageBounds = new(southwest.Longitude, southwest.Latitude,
            northeast.Longitude, northeast.Latitude);
        using SqliteGeodataFixture fixture = new([river, bridge, building], null, bounds: packageBounds);
        RoutingTestLogger logger = new();
        GridRoutePlanner planner = new(fixture.Repository, new GridGenerator(), new AStarGridPathfinder(), logger);
        GeoCoordinate[] points = [TestData.P(-500, 0), TestData.P(500, 30)];

        IReadOnlyList<RouteAlternative> routes = await planner.PlanAsync(points, new(25, 100_000), default);

        Assert.NotEmpty(routes);
        RoutingLogEntry successfulMesh = logger.Events.Last(entry => entry.Event.Id == 5);
        GeoBounds loadedBounds = new(Assert.IsType<double>(successfulMesh.Fields["West"]),
            Assert.IsType<double>(successfulMesh.Fields["South"]),
            Assert.IsType<double>(successfulMesh.Fields["East"]),
            Assert.IsType<double>(successfulMesh.Fields["North"]));
        IReadOnlyList<MapFeature> loadedFeatures = await fixture.Repository.QueryAsync(loadedBounds, default);
        Assert.DoesNotContain(loadedFeatures, feature => feature.Id == building.Id);
        Assert.Contains(logger.Events, entry => entry.Event.Id == 9
            && Assert.IsType<bool>(entry.Fields["Found"]));
        Geometry obstacle = new WKBReader().Read(building.Wkb);
        GeometryFactory factory = new();
        Assert.All(routes, route =>
        {
            Assert.Equal(points[0], route.Geometry[0]);
            Assert.Equal(points[^1], route.Geometry[^1]);
            LineString geometry = factory.CreateLineString(route.Geometry
                .Select(point => new Coordinate(point.Longitude, point.Latitude)).ToArray());
            Assert.True(geometry.Disjoint(obstacle));
            Assert.All(route.Geometry, point => Assert.True(loadedBounds.Contains(point)));
        });
    }
}
