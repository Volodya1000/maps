using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class LargeScaleRoutingTests
{
    [Fact]
    public async Task ValidDirectRouteSurvivesAnUnresolvedGridForOtherProfiles()
    {
        MapFeature lower = TestData.Polygon(1, TerrainKind.Lake, -100, -20, 700, 149);
        MapFeature upper = TestData.Polygon(2, TerrainKind.Lake, -100, 151, 700, 1000);
        IReadOnlyList<RouteAlternative> routes = await TestData.Planner(lower, upper).PlanAsync(
            [TestData.P(40, 150), TestData.P(280, 150)], TestData.Options, CancellationToken.None);
        RouteAlternative route = Assert.Single(routes);
        Assert.Equal("Короче", route.Name);
        Assert.InRange(route.Metrics.LengthMetres, 239, 241);
        var line = new LineString(route.Geometry.Select(point => new Coordinate(point.Longitude, point.Latitude)).ToArray());
        Assert.False(line.Intersects(new WKBReader().Read(lower.Wkb)));
        Assert.False(line.Intersects(new WKBReader().Read(upper.Wkb)));
    }

    [Fact]
    public async Task NoRouteReportsAnUnresolvedSearchAreaRatherThanAssumingWater()
    {
        MapFeature barrier = TestData.Polygon(1, TerrainKind.Building, 140, -1000, 180, 1000);
        PlanningException error = await Assert.ThrowsAsync<PlanningException>(() => TestData.Planner(barrier).PlanAsync(
            [TestData.P(40, 150), TestData.P(280, 150)], TestData.Options, CancellationToken.None));
        Assert.Equal(PlanningErrorReason.NoRouteInSearchArea, error.Reason);
    }

    [Theory]
    [InlineData(53.9045, 27.5615, 52.0978, 23.6877)]
    [InlineData(55.1904, 30.2049, 52.4345, 30.9754)]
    public async Task IntercityDistanceIsSupportedOnUnobstructedSyntheticTerrain(double fromLat, double fromLon, double toLat, double toLon)
    {
        var from = new GeoCoordinate(fromLat, fromLon);
        var to = new GeoCoordinate(toLat, toLon);
        var planner = new GridRoutePlanner(new EmptyCountryData(), new GridGenerator(),
            new AStarGridPathfinder(), NullLogger<GridRoutePlanner>.Instance);
        IReadOnlyList<RouteAlternative> routes = await planner.PlanAsync([from, to], new RoutingOptions(), CancellationToken.None);
        Assert.NotEmpty(routes);
        RouteAlternative shortest = Assert.Single(routes, route => route.Name == "Короче");
        Assert.Equal(from.DistanceTo(to), shortest.Metrics.LengthMetres, 4);
        Assert.All(routes, route =>
        {
            Assert.Equal(from, route.Geometry[0]);
            Assert.Equal(to, route.Geometry[^1]);
            Assert.True(route.Metrics.LengthMetres >= from.DistanceTo(to) - 0.01);
        });
    }

    private sealed class EmptyCountryData : IGeodataRepository
    {
        public DataPackageInfo Info { get; } = new("synthetic-country", new(23.17, 51.25, 32.79, 56.18), "Synthetic, not OSM", false, "tests");
        public Task<IReadOnlyList<MapFeature>> QueryAsync(GeoBounds bounds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MapFeature>>([]);
    }
}
