using System.Text.Json;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class RoutingBaselineTests
{
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
    [InlineData("unmapped-road")]
    [InlineData("length-limit")]
    [InlineData("adaptive-grid")]
    public async Task ControlRoutesMatchMainBeforeRefactoring(string scenario)
    {
        (IRoutePlanner planner, GeoCoordinate[] points, RoutingOptions options) = Scenario(scenario);
        JsonElement actual;
        try
        {
            IReadOnlyList<RouteAlternative> routes = await planner.PlanAsync(points, options, default);
            actual = JsonSerializer.SerializeToElement(routes.Select(route => new
            {
                route.Name,
                route.Geometry,
                route.Metrics,
                route.TransitionPoints,
                route.RoadContacts,
                route.GasPipelineCandidateIds
            }), JsonOptions);
        }
        catch (PlanningException error)
        {
            actual = JsonSerializer.SerializeToElement(new
            {
                error.Reason,
                error.TerrainKind,
                error.ActualRouteLengthMetres,
                error.MaximumRouteLengthMetres,
                Failures = error.SearchFailures.Select(failure => new { failure.Profile, failure.Error.Reason })
            }, JsonOptions);
        }

        // Explicit one-time recording on the documented baseline revision only.
        // CI never sets this variable and always checks the committed fixtures.
        string? recordDirectory = Environment.GetEnvironmentVariable("MAPS_ROUTING_RECORD_BASELINE");
        if (recordDirectory is not null)
        {
            Directory.CreateDirectory(recordDirectory);
            File.WriteAllText(Path.Combine(recordDirectory, scenario + ".json"), actual.GetRawText() + Environment.NewLine);
            return;
        }
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "RoutingBaseline", scenario + ".json");
        using JsonDocument expected = JsonDocument.Parse(File.ReadAllText(path));
        Compare(expected.RootElement, actual, scenario);
    }

    private static void Compare(JsonElement expected, JsonElement actual, string path)
    {
        Assert.Equal(expected.ValueKind, actual.ValueKind);
        switch (expected.ValueKind)
        {
            case JsonValueKind.Array:
                Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength());
                for (int index = 0; index < expected.GetArrayLength(); index++)
                { Compare(expected[index], actual[index], $"{path}[{index}]"); }
                break;
            case JsonValueKind.Object:
                Assert.Equal(expected.EnumerateObject().Select(property => property.Name),
                    actual.EnumerateObject().Select(property => property.Name));
                foreach (JsonProperty property in expected.EnumerateObject())
                { Compare(property.Value, actual.GetProperty(property.Name), path + "." + property.Name); }
                break;
            case JsonValueKind.Number:
                if (expected.TryGetInt64(out long expectedInteger) && actual.TryGetInt64(out long actualInteger))
                { Assert.Equal(expectedInteger, actualInteger); }
                else
                {
                    double tolerance = path.EndsWith(".Latitude", StringComparison.Ordinal)
                        || path.EndsWith(".Longitude", StringComparison.Ordinal) ? 1e-9 : 1e-6;
                    Assert.True(Math.Abs(expected.GetDouble() - actual.GetDouble()) <= tolerance,
                        $"{path}: expected {expected}, actual {actual}, tolerance {tolerance}");
                }
                break;
            default:
                Assert.Equal(expected.GetRawText(), actual.GetRawText());
                break;
        }
    }

    private static (IRoutePlanner Planner, GeoCoordinate[] Points, RoutingOptions Options) Scenario(string name)
    {
        List<MapFeature> features = [];
        GeoCoordinate[] points = [TestData.P(50, 150), TestData.P(280, 150)];
        RoutingOptions options = TestData.Options;
        switch (name)
        {
            case "empty":
                break;
            case "building":
            case "marsh":
            case "lake":
                TerrainKind kind = name switch
                {
                    "building" => TerrainKind.Building,
                    "marsh" => TerrainKind.Marsh,
                    _ => TerrainKind.Lake
                };
                features.Add(TestData.Polygon(1, kind, 120, 80, 180, 240));
                break;
            case "approved-river":
            case "curved-bridge":
                features.Add(TestData.Polygon(1, TerrainKind.River, 140, -100, 180, 1000));
                features.Add(name == "approved-river"
                    ? TestData.Polygon(2, TerrainKind.ApprovedCrossing, 130, 140, 190, 160, "surveyed-1")
                    : TestData.Line(2, TerrainKind.BridgeCandidate, (110, 150), (150, 170), (190, 150)));
                break;
            case "road-bridge":
            case "unmapped-road":
                features.Add(TestData.Line(1, TerrainKind.Road, (150, -5000), (150, 5000)));
                if (name == "road-bridge")
                { features.Add(TestData.Line(2, TerrainKind.BridgeCandidate, (130, 150), (170, 150))); }
                break;
            case "gas-off":
            case "gas-on":
                features.Add(TestData.Line(1, TerrainKind.Road, (-100, 150), (700, 150)));
                features.Add(TestData.Line(2, TerrainKind.GasPipelineCrossingCandidate,
                    (148, 138), (160, 138), (160, 150), (160, 162), (172, 162)));
                points = [TestData.P(40, 100), TestData.P(280, 200)];
                options = options with { PreferOverheadGasCrossings = name == "gas-on", CellSizeMetres = 80 };
                break;
            case "terrain-profiles":
                features.Add(TestData.Polygon(1, TerrainKind.Forest, 100, 60, 220, 240));
                features.Add(TestData.Polygon(2, TerrainKind.Field, 20, 0, 300, 80));
                features.Add(TestData.Polygon(3, TerrainKind.Clearing, 30, 250, 300, 300));
                break;
            case "waypoints":
                points = [TestData.P(13, 17), TestData.P(13, 17), TestData.P(145, 247), TestData.P(293, 33)];
                break;
            case "coverage-notch":
                return (CoverageTestData.Planner(CoverageTestData.Repository()),
                    [TestData.P(50, 500), TestData.P(300, 500)], options);
            case "length-limit":
                options = options with { MaxRouteLengthMetres = 30 };
                break;
            case "adaptive-grid":
                MemoryData data = new() { Info = new MemoryData().Info with { Bounds = new(26, 53, 29, 56) } };
                return (CoverageTestData.Planner(data), [new(54, 27), new(54.001, 27.6)],
                    options with { CellSizeMetres = 200, MaxNodes = 3000 });
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown control scenario.");
        }
        return (TestData.Planner(features.ToArray()), points, options);
    }
}
