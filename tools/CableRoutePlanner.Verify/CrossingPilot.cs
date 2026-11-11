using System.Diagnostics;
using System.Text.Json;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure;
using CableRoutePlanner.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CableRoutePlanner.Verify;

internal static class CrossingPilot
{
    public static async Task<int> RunAsync(string data, string input, string output)
    {
        JsonElement scenario = JsonDocument.Parse(File.ReadAllText(input)).RootElement;
        JsonElement coordinates = scenario.GetProperty("points");
        if (coordinates.GetArrayLength() != 2)
        {
            throw new ArgumentException($"points: expected exactly 2, got {coordinates.GetArrayLength()}", nameof(input));
        }
        GeoCoordinate[] points = coordinates.EnumerateArray().Select(point => new GeoCoordinate(
            point.GetProperty("Latitude").GetDouble(), point.GetProperty("Longitude").GetDouble())).ToArray();
        RoutingOptions options = scenario.GetProperty("routingOptions").Deserialize<RoutingOptions>()
            ?? throw new ArgumentException("routingOptions: null", nameof(input));
        options.Validate();
        ServiceCollection services = new();
        services.AddLogging(logging => logging.AddConsole());
        services.AddInfrastructure();
        services.AddPersistence(data, Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
        services.AddSingleton<IPlanningSession, PlanningSession>();
        using ServiceProvider provider = services.BuildServiceProvider();
        IPlanningSession session = provider.GetRequiredService<IPlanningSession>();
        Stopwatch timer = Stopwatch.StartNew();
        object result;
        try
        {
            foreach (GeoCoordinate point in points)
            {
                await session.AddAsync(point, default);
            }
            IReadOnlyList<RouteAlternative> routes = await session.BuildAsync(options, default);
            result = new
            {
                status = "success",
                elapsedMilliseconds = timer.ElapsedMilliseconds,
                alternatives = routes.Select(route => new
                {
                    name = route.Name,
                    geometry = route.Geometry,
                    metrics = route.Metrics,
                    roadContacts = route.RoadContacts,
                    gasCandidateIds = route.GasPipelineCandidateIds
                }),
                error = (object?)null
            };
        }
        catch (PlanningException error)
        {
            string status = error.Reason switch
            {
                PlanningErrorReason.PointOutsidePackage or PlanningErrorReason.BlockedTerrain => "invalid_input",
                PlanningErrorReason.GridConnectionUnavailable or PlanningErrorReason.NoRouteInSearchArea
                    or PlanningErrorReason.RouteLengthLimitExceeded => "no_route",
                PlanningErrorReason.ResourceLimit => "resource_limit",
                _ => "error"
            };
            result = new
            {
                status,
                elapsedMilliseconds = timer.ElapsedMilliseconds,
                alternatives = Array.Empty<object>(),
                error = new
                {
                    reason = error.Reason.ToString(),
                    terrainKind = error.TerrainKind?.ToString(),
                    searchFailures = error.SearchFailures.Select(failure => new
                    {
                        profile = failure.Profile,
                        reason = failure.Error.Reason.ToString()
                    })
                }
            };
        }
        File.WriteAllText(output, JsonSerializer.Serialize(result, VerifyJson.Options));
        return 0;
    }
}
