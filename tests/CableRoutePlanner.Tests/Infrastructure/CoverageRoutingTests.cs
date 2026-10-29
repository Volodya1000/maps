using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure;
using NetTopologySuite.Geometries;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class CoverageRoutingTests
{
    [Fact]
    public async Task FullyCoveredLocalSearchPreservesTheDirectRoute()
    {
        GeoCoverage coverage = new([TestData.P(-1000, -1000), TestData.P(2000, -1000),
            TestData.P(2000, 2000), TestData.P(-1000, 2000), TestData.P(-1000, -1000)]);
        var data = new MemoryData { Info = new MemoryData().Info with { Coverage = coverage } };
        GeoCoordinate[] points = [TestData.P(50, 100), TestData.P(300, 500)];

        IReadOnlyList<RouteAlternative> routes = await CoverageTestData.Planner(data).PlanAsync(points, TestData.Options, default);

        Assert.NotEmpty(routes);
        Assert.Equal(2, routes[0].Geometry.Count);
        double expected = points[0].DistanceTo(points[1]);
        Assert.InRange(routes[0].Metrics.LengthMetres, expected - 0.001, expected + 0.001);
    }

    [Fact]
    public async Task InsideToInsideRouteCannotShortcutAnUncoveredBorderNotch()
    {
        MemoryData data = CoverageTestData.Repository();
        IRoutePlanner planner = CoverageTestData.Planner(data);
        IReadOnlyList<RouteAlternative> routes = await planner.PlanAsync(
            [TestData.P(50, 500), TestData.P(300, 500)], TestData.Options, default);
        Polygon polygon = new GeometryFactory().CreatePolygon(data.Info.Coverage!.Boundary
            .Select(point => new Coordinate(point.Longitude, point.Latitude)).ToArray());

        Assert.NotEmpty(routes);
        Assert.All(routes, route =>
        {
            LineString geometry = new(route.Geometry.Select(point => new Coordinate(point.Longitude, point.Latitude)).ToArray());
            Assert.True(polygon.Buffer(1e-10).Covers(geometry));
            Assert.True(route.Metrics.LengthMetres > 600);
        });
    }

    [Fact]
    public async Task UncoveredAreaCannotBypassAnObstacleAcrossTheOnlyCoveredCorridor()
    {
        MemoryData template = CoverageTestData.Repository();
        MapFeature building = TestData.Polygon(1, TerrainKind.Building, 100, -10, 250, 210);
        var data = new MemoryData(building) { Info = template.Info };

        PlanningException error = await Assert.ThrowsAsync<PlanningException>(() => CoverageTestData.Planner(data).PlanAsync(
            [TestData.P(50, 500), TestData.P(300, 500)], TestData.Options, default));

        Assert.Equal(PlanningErrorReason.NoRouteInSearchArea, error.Reason);
    }

    [Fact]
    public async Task PlannerRejectsUncoveredWaypointInsideBoundingRectangle()
    {
        IRoutePlanner planner = CoverageTestData.Planner(CoverageTestData.Repository());
        PlanningException error = await Assert.ThrowsAsync<PlanningException>(() => planner.PlanAsync(
            [TestData.P(50, 500), TestData.P(175, 500)], TestData.Options, default));
        Assert.Equal(PlanningErrorReason.PointOutsidePackage, error.Reason);
    }

    [Fact]
    public async Task MovingOutsideCoveragePreservesPointAndRevision()
    {
        MemoryData data = CoverageTestData.Repository();
        var session = new PlanningSession(CoverageTestData.Planner(data), new GeodataPointPlacementValidator(data));
        Waypoint point = await session.AddAsync(TestData.P(50, 500), default);
        long revision = session.Revision;

        PlanningException error = await Assert.ThrowsAsync<PlanningException>(() => session.MoveAsync(point.Id, TestData.P(175, 500), default));

        Assert.Equal(PlanningErrorReason.PointOutsidePackage, error.Reason);
        Assert.Equal(point, Assert.Single(session.Points));
        Assert.Equal(revision, session.Revision);
    }
}
