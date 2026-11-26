using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using CableRoutePlanner.Infrastructure.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class SourceRouteRecoveryTests
{
    [Theory]
    [InlineData("mass-naliboki-bridge-02")]
    [InlineData("mass-gomel-bridge-01")]
    [InlineData("mass-gomel-bridge-03")]
    [InlineData("mass-gomel-bridge-05")]
    [InlineData("mass-naliboki-multi-06")]
    [InlineData("mass-smolevichi-no_candidate-11")]
    [InlineData("mass-pukhovichi-multi-07")]
    public async Task OriginalSourceEndpointsFindValidRouteThroughMappedTerrain(string caseId)
    {
        using SourceRoutingCaseFixture fixture = new(caseId);
        GridRoutePlanner planner = new(fixture.Repository, new GridGenerator(), new AStarGridPathfinder(),
            NullLogger<GridRoutePlanner>.Instance);

        // Only the original two endpoints and original options enter the planner.
        // The independently recorded witness remains evidence inside the fixture.
        IReadOnlyList<RouteAlternative> routes = await planner.PlanAsync(fixture.Points, fixture.Options, default);

        Assert.NotEmpty(routes);
        Assert.All(routes, route =>
        {
            Assert.True(double.IsFinite(route.Metrics.LengthMetres));
            Assert.InRange(route.Metrics.LengthMetres, fixture.Points[0].DistanceTo(fixture.Points[^1]),
                fixture.Options.MaxRouteLengthMetres);
            Assert.True(double.IsFinite(route.Metrics.WeightedCost));
            Assert.Equal(0, route.Metrics.ApprovedWaterCrossings);
            Assert.Empty(route.UnderpassCandidateIds ?? []);
            double measuredLength = AssertValidGeometry(route.Geometry, fixture);
            Assert.Equal(measuredLength, route.Metrics.LengthMetres, precision: 6);
        });
    }

    [Theory]
    [InlineData("mass-naliboki-bridge-02")]
    [InlineData("mass-gomel-bridge-01")]
    [InlineData("mass-gomel-bridge-03")]
    [InlineData("mass-gomel-bridge-05")]
    [InlineData("mass-naliboki-multi-06")]
    [InlineData("mass-smolevichi-no_candidate-11")]
    [InlineData("mass-pukhovichi-multi-07")]
    public void KnownWitnessPassesOriginalTerrainRules(string caseId)
    {
        using SourceRoutingCaseFixture fixture = new(caseId);

        double length = AssertValidGeometry(fixture.Witness, fixture);

        Assert.True(double.IsFinite(length));
        Assert.InRange(length, fixture.Points[0].DistanceTo(fixture.Points[^1]), fixture.Options.MaxRouteLengthMetres);
    }

    private static double AssertValidGeometry(IReadOnlyList<GeoCoordinate> geometry, SourceRoutingCaseFixture fixture)
    {
        Assert.True(geometry.Count >= 2);
        Assert.Equal(fixture.Points[0], geometry[0]);
        Assert.Equal(fixture.Points[^1], geometry[^1]);
        GeoBounds bounds = fixture.SourceQueryBounds;
        LocalProjection projection = new(new GeoCoordinate((bounds.South + bounds.North) / 2,
            (bounds.West + bounds.East) / 2));
        GeometryFactory factory = new();
        Polygon coverage = factory.CreatePolygon(fixture.Coverage.Boundary.Select(projection.Forward).ToArray());
        TerrainEvaluator evaluator = new(fixture.Features, projection,
            preferGas: fixture.Options.PreferOverheadGasCrossings, coverage: coverage,
            allowUnderpasses: fixture.Options.AllowExperimentalPedestrianUnderpasses);
        WKBReader reader = new();
        (MapFeature Feature, Geometry Geometry)[] features = fixture.Features
            .Select(feature => (feature, projection.Forward(reader.Read(feature.Wkb)))).ToArray();
        LineString path = new(geometry.Select(projection.Forward).ToArray());
        Assert.True(coverage.Covers(path));
        double measuredLength = 0;
        for (int index = 1; index < geometry.Count; index++)
        {
            GeoCoordinate from = geometry[index - 1];
            GeoCoordinate to = geometry[index];
            Assert.True(double.IsFinite(from.Latitude) && double.IsFinite(from.Longitude));
            Assert.True(double.IsFinite(to.Latitude) && double.IsFinite(to.Longitude));
            Assert.NotNull(evaluator.Evaluate(projection.Forward(from), projection.Forward(to)));
            measuredLength += from.DistanceTo(to);
        }

        // Check the returned geometry directly against source WKB as well as the
        // evaluator. These cases contain no approved or underground crossings.
        Geometry[] bridges = features.Where(feature => feature.Feature.Kind == TerrainKind.BridgeCandidate)
            .Select(feature => feature.Geometry).ToArray();
        foreach ((MapFeature feature, Geometry obstacle) in features)
        {
            if (feature.Kind is TerrainKind.Building or TerrainKind.Marsh)
            {
                Assert.False(obstacle.Intersects(path));
            }
            else if (feature.Kind is TerrainKind.River or TerrainKind.Lake)
            {
                Geometry closedWater = obstacle;
                foreach (Geometry bridge in bridges.Where(obstacle.Intersects))
                {
                    closedWater = closedWater.Difference(bridge.Buffer(PreparedTerrainData.BridgeCorridorToleranceMetres));
                }
                Assert.False(closedWater.Intersects(path));
            }
            else if (feature.Kind == TerrainKind.Road)
            {
                for (int index = 1; index < path.NumPoints; index++)
                {
                    LineString segment = new([path.GetCoordinateN(index - 1), path.GetCoordinateN(index)]);
                    foreach (Point contact in PointContacts(obstacle.Intersection(segment)))
                    {
                        Assert.Contains(bridges, bridge => bridge.Intersects(obstacle)
                            && bridge.Buffer(PreparedTerrainData.BridgeCorridorToleranceMetres).Covers(contact));
                    }
                }
            }
        }
        return measuredLength;
    }

    private static IEnumerable<Point> PointContacts(Geometry geometry) => geometry switch
    {
        Point point when !point.IsEmpty => [point],
        GeometryCollection collection => collection.Geometries.SelectMany(PointContacts),
        _ => []
    };
}
