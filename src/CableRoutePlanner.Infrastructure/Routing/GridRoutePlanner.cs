using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;

namespace CableRoutePlanner.Infrastructure.Routing;

/// <summary>Coordinates package-bounded preparation, grid generation and ordered engineering cost profiles.</summary>
/// <remarks>
/// The planner owns one reusable prepared area. Search graphs, measured edge caches
/// and result builders belong to each calculation, including concurrent requests.
/// Geometry operations and alternative metrics have separate internal owners.
/// </remarks>
public sealed class GridRoutePlanner(
    IGeodataRepository data,
    IGridGenerator gridGenerator,
    IGridPathfinder pathfinder,
    ILogger<GridRoutePlanner> logger) : IRoutePlanner
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private readonly record struct SearchMargins(double LatitudeDegrees, double LongitudeDegrees);

    private const double MinimumSearchMarginMetres = 1_000;
    private const double MaximumSearchMarginMetres = 5_000;

    private readonly PreparedRoutingAreaCache _areas = new(data);

    /// <summary>Builds permitted, distinct alternatives through the points in their supplied order.</summary>
    /// <param name="points">Geographic waypoints inside the package coverage.</param>
    /// <param name="options">Validated grid/resource settings and inclusive route-length limit.</param>
    /// <param name="cancellationToken">Cancels preparation or search without publishing partial results.</param>
    /// <returns>Read-only alternatives in profile order, stably prioritizing gas when requested.</returns>
    /// <exception cref="PlanningException">Input, resource limits or every profile prevent a usable route.</exception>
    /// <exception cref="OperationCanceledException">The calculation was cancelled.</exception>
    public async Task<IReadOnlyList<RouteAlternative>> PlanAsync(
        IReadOnlyList<GeoCoordinate> points,
        RoutingOptions options,
        CancellationToken cancellationToken)
    {
        options.Validate();
        if (points.Count < 2)
        {
            throw new PlanningException("Установите минимум две точки.");
        }

        if (points.Any(p => !data.Info.Contains(p)))
        {
            throw new PlanningException(
                "Для выбранной точки нет данных в офлайн-пакете.", PlanningErrorReason.PointOutsidePackage);
        }

        SearchMargins margins = SearchMarginDegrees(points);
        GeoBounds requested = new(
            points.Min(point => point.Longitude) - margins.LongitudeDegrees,
            points.Min(point => point.Latitude) - margins.LatitudeDegrees,
            points.Max(point => point.Longitude) + margins.LongitudeDegrees,
            points.Max(point => point.Latitude) + margins.LatitudeDegrees);
        GeoBounds coverage = data.Info.Bounds;
        var bounds = new GeoBounds(
            Math.Max(requested.West, coverage.West),
            Math.Max(requested.South, coverage.South),
            Math.Min(requested.East, coverage.East),
            Math.Min(requested.North, coverage.North));
        PreparedRoutingArea area = await _areas.GetAsync(bounds, cancellationToken);
        RoutingLog.PreparationLog(logger, area.QueryMilliseconds, area.PreparationMilliseconds);

        Stopwatch calculationTimer = Stopwatch.StartNew();
        IReadOnlyList<RouteAlternative> routes = await Task.Run<IReadOnlyList<RouteAlternative>>(
            () => Calculate(points, bounds, area, options, cancellationToken),
            cancellationToken);
        RoutingLog.RoutesCompletedLog(logger, routes.Count, points.Count, calculationTimer.ElapsedMilliseconds);
        return routes;
    }

    /// <summary>Converts a bounded detour allowance in metres to separate latitude/longitude margins.</summary>
    /// <remarks>
    /// Ten percent of the longest consecutive leg is clamped to 1–5 km. The
    /// longitude correction uses the mean latitude's cosine and caps at 180 degrees
    /// near the poles. This bounds loaded data; it does not guarantee search completeness.
    /// </remarks>
    private static SearchMargins SearchMarginDegrees(IReadOnlyList<GeoCoordinate> points)
    {
        double spanMetres = points
            .Zip(points.Skip(1), (from, to) => from.DistanceTo(to))
            .DefaultIfEmpty()
            .Max();
        double marginMetres = Math.Clamp(
            Math.Max(MinimumSearchMarginMetres, spanMetres * 0.1),
            MinimumSearchMarginMetres,
            MaximumSearchMarginMetres);
        double latitude = points.Average(point => point.Latitude);
        double latitudeDegrees = marginMetres / (GeoCoordinate.MeanEarthRadiusMetres * Math.PI / 180);
        double longitudeScale = Math.Abs(Math.Cos(latitude * Math.PI / 180));
        double longitudeDegrees = longitudeScale > double.Epsilon
            ? Math.Min(180, latitudeDegrees / longitudeScale)
            : 180;
        return new(latitudeDegrees, longitudeDegrees);
    }

    private ReadOnlyCollection<RouteAlternative> Calculate(
        IReadOnlyList<GeoCoordinate> points,
        GeoBounds bounds,
        PreparedRoutingArea area,
        RoutingOptions options,
        CancellationToken ct)
    {
        LocalProjection projection = area.Projection;
        TerrainEvaluator evaluator = new(area.Terrain, preferGas: options.PreferOverheadGasCrossings);
        RouteGeometryPostProcessor postProcessor = new(evaluator, area.Terrain);
        GridMesh mesh = gridGenerator.Generate(bounds, options, projection);
        Coordinate maximum = projection.Forward(new GeoCoordinate(bounds.North, bounds.East));
        mesh = mesh with
        {
            // A portal outside the queried area could bypass obstacles we never loaded.
            Portals = area.Terrain.TransitionPortals.Where(portal => portal.Coordinates.All(point =>
                point.X >= mesh.Minimum.X && point.Y >= mesh.Minimum.Y
                && point.X <= maximum.X && point.Y <= maximum.Y)).ToArray()
        };
        RouteGeometryBuilder geometryBuilder = new(pathfinder, mesh, evaluator, projection, postProcessor);

        RouteAlternativeBuilder alternatives = new(evaluator, postProcessor, area.Terrain, options);
        foreach (CostProfile profile in CostProfile.Defaults)
        {
            ct.ThrowIfCancellationRequested();
            profile.Validate();
            List<Coordinate> route;
            try
            {
                route = geometryBuilder.Build(points, profile, ct);
            }
            catch (PlanningException error) when (error.Reason is
                PlanningErrorReason.GridConnectionUnavailable or PlanningErrorReason.NoRouteInSearchArea)
            {
                // Try other profiles, retaining the actual diagnostic even if no
                // earlier profile succeeded. Unexpected exceptions still propagate.
                alternatives.RecordFailure(profile, error);
                RoutingLog.ProfileUnavailableLog(logger, profile.Name, error.Reason, error);
                continue;
            }

            PlanningException? rejection = alternatives.Add(route, profile, ct);
            if (rejection is not null)
            {
                alternatives.RecordFailure(profile, rejection);
                RoutingLog.ProfileUnavailableLog(logger, profile.Name, rejection.Reason, rejection);
            }
        }

        ReadOnlyCollection<RouteAlternative> result = alternatives.Complete();
        RoutingLog.SearchCompletedLog(logger, result.Count, mesh.Nodes.Length, geometryBuilder.Edges.EvaluationCount);
        return result;
    }
}
