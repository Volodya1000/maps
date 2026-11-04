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
        RoutingControlCase input = RoutingControlCase.Create(scenario);
        IRoutePlanner planner = CoverageTestData.Planner(input.Data);
        GeoCoordinate[] points = input.Points;
        RoutingOptions options = input.Options;
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
        RoutingGolden.Compare(expected.RootElement, actual, scenario);
    }


}
