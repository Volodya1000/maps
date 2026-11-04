using CableRoutePlanner.Application;
using Microsoft.Extensions.Logging;

namespace CableRoutePlanner.Infrastructure.Routing;

/// <summary>Structured routing events; IDs, names and fields remain stable for diagnostic consumers.</summary>
internal static partial class RoutingLog
{
    [LoggerMessage(EventId = 1, EventName = nameof(PreparationLog), Level = LogLevel.Information,
        Message = "Routing SQLite query {QueryMilliseconds} ms; WKB, projection and spatial index {PreparationMilliseconds} ms (zero on cache reuse)")]
    public static partial void PreparationLog(ILogger logger, long queryMilliseconds, long preparationMilliseconds);

    [LoggerMessage(EventId = 2, EventName = nameof(RoutesCompletedLog), Level = LogLevel.Information,
        Message = "Completed {RouteCount} routes for {WaypointCount} waypoints in {ElapsedMilliseconds} ms")]
    public static partial void RoutesCompletedLog(ILogger logger, int routeCount, int waypointCount, long elapsedMilliseconds);

    [LoggerMessage(EventId = 3, EventName = nameof(SearchCompletedLog), Level = LogLevel.Information,
        Message = "Calculated {RouteCount} distinct routes over {NodeCount} grid nodes and evaluated {EdgeEvaluations} grid edges")]
    public static partial void SearchCompletedLog(ILogger logger, int routeCount, int nodeCount, int edgeEvaluations);

    [LoggerMessage(EventId = 4, EventName = nameof(ProfileUnavailableLog), Level = LogLevel.Information,
        Message = "Profile {Profile} is unavailable ({Reason})")]
    public static partial void ProfileUnavailableLog(ILogger logger, string profile, PlanningErrorReason reason, Exception error);
}
