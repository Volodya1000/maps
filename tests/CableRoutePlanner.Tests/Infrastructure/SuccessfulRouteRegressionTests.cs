using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure;
using CableRoutePlanner.Infrastructure.Geography;
using CableRoutePlanner.Infrastructure.Routing;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Xunit;
using Xunit.Abstractions;

namespace CableRoutePlanner.Tests;

public sealed class SuccessfulRouteRegressionTests(ITestOutputHelper output)
{
    private const double CoordinateToleranceMetres = 0.0002;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Theory]
    [InlineData("empty")]
    [InlineData("building")]
    [InlineData("marsh")]
    [InlineData("lake")]
    [InlineData("approved-river")]
    [InlineData("curved-bridge")]
    [InlineData("road-bridge")]
    [InlineData("gas-off")]
    [InlineData("gas-on")]
    [InlineData("terrain-profiles")]
    [InlineData("waypoints")]
    [InlineData("coverage-notch")]
    [InlineData("adaptive-grid")]
    [InlineData("minsk-direct")]
    [InlineData("minsk-bridge")]
    [InlineData("minsk-waypoints")]
    public async Task SuccessfulControlsPreserveGeometryEngineeringFactsAndProfileCosts(string scenario)
    {
        RoutingControlCase input = RoutingControlCase.Create(scenario);
        RecordingGridGenerator grid = new();
        RoutingTestLogger logger = new();
        GridRoutePlanner planner = new(input.Data, grid, new AStarGridPathfinder(), logger);
        GeodataPointPlacementValidator placement = new(input.Data);
        foreach (GeoCoordinate point in input.Points)
        { await placement.ValidateAsync(point, default); }

        using Process process = Process.GetCurrentProcess();
        long initialMemory = process.PeakWorkingSet64;
        Stopwatch timer = Stopwatch.StartNew();
        IReadOnlyList<RouteAlternative> routes = await planner.PlanAsync(input.Points, input.Options, default);
        double coldMilliseconds = timer.Elapsed.TotalMilliseconds;
        Assert.NotEmpty(routes);
        LocalProjection projection = Assert.IsType<LocalProjection>(grid.Projection);
        IReadOnlyList<MapFeature> features = await input.Data.QueryAsync(grid.Bounds, default);
        GeometryFactory factory = new();
        Geometry? coverage = input.Data.Info.Coverage is { } outline
            ? factory.CreatePolygon(outline.Boundary.Select(projection.Forward).ToArray()) : null;
        PreparedTerrainData terrain = new(features, projection, coverage);
        TerrainEvaluator evaluator = new(terrain, preferGas: input.Options.PreferOverheadGasCrossings);
        WKBReader reader = new();
        (MapFeature Feature, Geometry Shape)[] raw = features.Select(feature =>
            (feature, projection.Forward(reader.Read(feature.Wkb)))).ToArray();
        List<object> snapshots = [];
        foreach (RouteAlternative route in routes)
        {
            CostProfile profile = Assert.Single(CostProfile.Defaults, candidate => candidate.Name == route.Name);
            Coordinate[] path = route.Geometry.Select(projection.Forward).ToArray();
            Assert.True(factory.CreateLineString(path).IsSimple);
            AssertWaypointOrder(input.Points, route.Geometry);
            AssertRawPermission(path, raw, coverage, factory);
            double physicalLength = route.Geometry.Zip(route.Geometry.Skip(1), (from, to) => from.DistanceTo(to)).Sum();
            Assert.Equal(physicalLength, route.Metrics.LengthMetres, 6);
            Assert.True(physicalLength <= input.Options.MaxRouteLengthMetres);
            SortedSet<long> roads = [], bridges = [], approvals = [], gas = [];
            double forest = 0, field = 0, expectedCost = 0;
            for (int index = 1; index < path.Length; index++)
            {
                EdgeEvaluation edge = Assert.IsType<EdgeEvaluation>(evaluator.Evaluate(path[index - 1], path[index]));
                roads.UnionWith(edge.Roads);
                bridges.UnionWith(edge.BridgeCandidates);
                approvals.UnionWith(edge.Crossings);
                gas.UnionWith(edge.GasCandidates);
                forest += edge.Forest;
                field += edge.Field;
                // Reconstruct the documented pricing law independently of production pricing.
                expectedCost += edge.Length - edge.Clearing * (1 - profile.Clearing)
                    + edge.Forest * profile.Forest + edge.Field * profile.Field
                    + (edge.Roads.Count + edge.BridgeCandidates.Count
                        - (input.Options.PreferOverheadGasCrossings ? edge.GasRoadCrossings : 0)) * profile.RoadCrossingMetres;
            }
            Assert.Equal(forest, route.Metrics.ForestMetres, 6);
            Assert.Equal(field, route.Metrics.FieldMetres, 6);
            Assert.Equal(expectedCost, route.Metrics.WeightedCost, 6);
            Assert.Equal(roads.Count, route.Metrics.RoadCrossings);
            Assert.Equal(bridges.Count, route.Metrics.PotentialBridgeCrossings);
            Assert.Equal(approvals.Count, route.Metrics.ApprovedWaterCrossings);
            Assert.Equal(gas.Count, route.Metrics.PotentialGasPipelineCrossings);
            Assert.Equal(gas, route.GasPipelineCandidateIds);
            Assert.All(bridges, id => Assert.Contains(features, feature => feature.Id == id && feature.Kind == TerrainKind.BridgeCandidate));
            Assert.All(gas, id => Assert.Contains(features, feature => feature.Id == id && feature.Kind == TerrainKind.GasPipelineCrossingCandidate));
            AssertMetadata(route, raw, projection, factory);
            if (scenario is "curved-bridge" or "road-bridge")
            { Assert.Equal(2, Assert.Single(bridges)); }
            if (scenario is "gas-off" or "gas-on")
            { Assert.Equal(2, Assert.Single(gas)); }
            snapshots.Add(new
            {
                route.Name,
                route.Geometry,
                route.Metrics,
                route.TransitionPoints,
                route.RoadContacts,
                route.GasPipelineCandidateIds,
                BridgeCandidateIds = bridges.ToArray()
            });
        }
        if (scenario == "terrain-profiles")
        {
            Assert.True(routes.Count > 1);
            Assert.False(routes[0].Geometry.SequenceEqual(routes[1].Geometry));
        }
        if (scenario.StartsWith("minsk-", StringComparison.Ordinal))
        {
            JsonElement actual = JsonSerializer.SerializeToElement(snapshots, JsonOptions);
            string? recordDirectory = Environment.GetEnvironmentVariable("MAPS_RECORD_SUCCESS_BASELINE");
            if (recordDirectory is not null)
            {
                Directory.CreateDirectory(recordDirectory);
                File.WriteAllText(Path.Combine(recordDirectory, scenario + ".json"), actual.GetRawText() + Environment.NewLine);
            }
            else
            {
                using JsonDocument expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                    AppContext.BaseDirectory, "Fixtures", "RoutingBaseline", scenario + ".json")));
                RoutingGolden.Compare(expected.RootElement, actual, scenario);
            }
        }

        timer.Restart();
        IReadOnlyList<RouteAlternative> warm = await planner.PlanAsync(input.Points, input.Options, default);
        double warmMilliseconds = timer.Elapsed.TotalMilliseconds;
        Assert.Equal(routes.Select(route => route.Geometry), warm.Select(route => route.Geometry));
        Assert.Equal(routes.Select(route => route.Metrics), warm.Select(route => route.Metrics));
        RoutingLogEntry preparation = logger.Events.Last(entry => entry.Event.Id == 1);
        Assert.Equal(0L, preparation.Fields["QueryMilliseconds"]);
        Assert.Equal(0L, preparation.Fields["PreparationMilliseconds"]);
        process.Refresh();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"successful-control: {scenario} alternatives={routes.Count} coldMs={coldMilliseconds:F3} warmMs={warmMilliseconds:F3} "
            + $"peakDeltaMiB={Math.Max(0, process.PeakWorkingSet64 - initialMemory) / 1048576d:F1}"));
    }

    private static void AssertWaypointOrder(IReadOnlyList<GeoCoordinate> waypoints, IReadOnlyList<GeoCoordinate> route)
    {
        Assert.True(waypoints[0].DistanceTo(route[0]) < CoordinateToleranceMetres);
        Assert.True(waypoints[^1].DistanceTo(route[^1]) < CoordinateToleranceMetres);
        int cursor = 0;
        foreach (GeoCoordinate waypoint in waypoints.Distinct())
        {
            while (cursor < route.Count && waypoint.DistanceTo(route[cursor]) >= CoordinateToleranceMetres)
            { cursor++; }
            Assert.True(cursor < route.Count);
        }
    }

    private static void AssertRawPermission(Coordinate[] path, (MapFeature Feature, Geometry Shape)[] raw,
        Geometry? coverage, GeometryFactory factory)
    {
        LineString line = factory.CreateLineString(path);
        if (coverage is not null)
        { Assert.True(coverage.Covers(line)); }
        Geometry[] water = raw.Where(feature => feature.Feature.Kind is TerrainKind.River or TerrainKind.Lake)
            .Select(feature => feature.Shape).ToArray();
        Geometry[] openings = raw.Where(feature => feature.Feature.Kind == TerrainKind.ApprovedCrossing
                && !string.IsNullOrWhiteSpace(feature.Feature.ApprovalId))
            .Select(feature => feature.Shape)
            .Concat(raw.Where(feature => feature.Feature.Kind == TerrainKind.BridgeCandidate
                && water.Any(area => area.Intersects(feature.Shape)))
                .Select(feature => feature.Shape.Buffer(2)))
            .ToArray();
        foreach ((MapFeature feature, Geometry shape) in raw)
        {
            if (feature.Kind is TerrainKind.Building or TerrainKind.Marsh)
            { Assert.False(shape.Intersects(line)); }
            if (feature.Kind is TerrainKind.River or TerrainKind.Lake)
            {
                Geometry contact = shape.Intersection(line);
                foreach (Geometry opening in openings)
                { contact = contact.Difference(opening); }
                Assert.True(contact.IsEmpty);
            }
        }
    }

    private static void AssertMetadata(RouteAlternative route, (MapFeature Feature, Geometry Shape)[] raw,
        LocalProjection projection, GeometryFactory factory)
    {
        foreach (RouteRoadContact contact in route.RoadContacts ?? [])
        {
            Geometry shape = Assert.Single(raw, feature => feature.Feature.Id == contact.RoadId
                && feature.Feature.Kind is TerrainKind.Road or TerrainKind.BridgeCandidate).Shape;
            Assert.All(contact.Geometry, point => Assert.True(
                shape.Distance(factory.CreatePoint(projection.Forward(point))) < CoordinateToleranceMetres));
        }
        foreach (GeoCoordinate marker in route.TransitionPoints ?? [])
        {
            Point point = factory.CreatePoint(projection.Forward(marker));
            Assert.Contains(raw, feature => feature.Feature.Kind == TerrainKind.BridgeCandidate
                && feature.Shape.Distance(point) < CoordinateToleranceMetres);
        }
    }

    private sealed class RecordingGridGenerator : IGridGenerator
    {
        public GeoBounds Bounds { get; private set; }
        public LocalProjection? Projection { get; private set; }
        public GridMesh Generate(GeoBounds bounds, RoutingOptions options, LocalProjection projection)
        {
            Bounds = bounds;
            Projection = projection;
            return new GridGenerator().Generate(bounds, options, projection);
        }
    }
}
