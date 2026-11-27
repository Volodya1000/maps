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

    [LoggerMessage(EventId = 5, EventName = nameof(SearchMeshLog), Level = LogLevel.Debug,
        Message = "Calculation {CalculationId}, attempt {Attempt}: mesh {CellSizeMetres} m (requested {RequestedCellSizeMetres}), {NodeCount} nodes in {Columns} columns and {Rows} rows; bounds {West},{South},{East},{North}; {PortalCount} portals")]
    public static partial void SearchMeshLog(ILogger logger, Guid calculationId, double requestedCellSizeMetres,
        double cellSizeMetres, int nodeCount, int columns, int rows, double west, double south, double east,
        double north, int portalCount, int attempt = 0);

    [LoggerMessage(EventId = 6, EventName = nameof(GraphConnectionsLog), Level = LogLevel.Debug,
        Message = "Calculation {CalculationId}, profile {Profile}, leg {LegIndex}: endpoint connections {StartConnections}/{DestinationConnections}; {PortalNodeCount} portal vertices, {PortalGridConnections} grid links, {PortalEdges} portal edges; {RejectedConnections} rejected connectors; direct {DirectConnection}")]
    public static partial void GraphConnectionsLog(ILogger logger, Guid calculationId, string profile, int legIndex,
        int startConnections, int destinationConnections, int portalNodeCount, int portalGridConnections,
        int portalEdges, int rejectedConnections, bool directConnection);

    [LoggerMessage(EventId = 7, EventName = nameof(LegSearchLog), Level = LogLevel.Debug,
        Message = "Calculation {CalculationId}, profile {Profile}, leg {LegIndex}: reason {Reason}, cancelled {Cancelled}; expanded {ExpandedNodes} nodes, {EdgeEvaluations} unique grid evaluations; direct {DirectRoute}")]
    public static partial void LegSearchLog(ILogger logger, Guid calculationId, string profile, int legIndex,
        PlanningErrorReason? reason, bool cancelled, int expandedNodes, int edgeEvaluations, bool directRoute);

    [LoggerMessage(EventId = 8, EventName = nameof(SearchAttemptLog), Level = LogLevel.Debug,
        Message = "Calculation {CalculationId}, attempt {Attempt}: connector radius {ConnectionRadiusCells} cells; surface recovery {SurfaceRecovery}")]
    public static partial void SearchAttemptLog(ILogger logger, Guid calculationId, int attempt,
        int connectionRadiusCells, bool surfaceRecovery);

    [LoggerMessage(EventId = 9, EventName = nameof(SurfaceDetourLog), Level = LogLevel.Debug,
        Message = "Calculation {CalculationId}, profile {Profile}, leg {LegIndex}: surface graph {NodeCount} nodes, expanded {ExpandedNodes}, input {InputVertexCount} vertices; resource limited {ResourceLimited}; found {Found}")]
    public static partial void SurfaceDetourLog(ILogger logger, Guid calculationId, string profile, int legIndex,
        int nodeCount, int expandedNodes, int inputVertexCount, bool resourceLimited, bool found);
}
