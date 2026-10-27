using CableRoutePlanner.Domain;

namespace CableRoutePlanner.ViewModels;

/// <summary>Projects route geometry into a compact numbered presentation list.</summary>
internal static class RouteVertexProjection
{
    private const double WaypointCoincidenceToleranceMetres = 1;
    private const double TransitionOnSegmentToleranceMetres = 2;
    private const double MinimumTurnSegmentMetres = 15;
    private const double SignificantTurnDegrees = 25;
    // Local metric approximation for display only; valid over the Belarus latitude range.
    private const double LongitudeMetresPerDegree = 111_320;
    private const double LatitudeMetresPerDegree = 111_132;

    public static IReadOnlyList<RouteVertexRow> Project(RouteAlternative route, IReadOnlyList<GeoCoordinate> waypoints)
    {
        var rows = new List<RouteVertexRow>();
        IReadOnlyList<GeoCoordinate> expandedGeometry = ExpandWithTransitionPoints(route);
        GeoCoordinate[] coordinates = expandedGeometry
            .Where((coordinate, index) => index == 0 || coordinate != expandedGeometry[index - 1])
            .ToArray();
        for (int index = 0; index < coordinates.Length; index++)
        {
            GeoCoordinate coordinate = coordinates[index];
            bool isEndpoint = index == 0 || index == coordinates.Length - 1;
            bool isWaypoint = waypoints.Any(point => point.DistanceTo(coordinate) <= WaypointCoincidenceToleranceMetres);
            bool isTransition = route.TransitionPoints?.Any(point => point.DistanceTo(coordinate) <= WaypointCoincidenceToleranceMetres) == true;
            bool isTurn = index > 0 && index < coordinates.Length - 1 && IsSignificantTurn(
                coordinates[index - 1], coordinate, coordinates[index + 1]);
            if ((isEndpoint || isWaypoint || isTransition || isTurn)
                && (rows.Count == 0 || rows[^1].Coordinate != coordinate))
            {
                RouteVertexKind kind = isWaypoint ? RouteVertexKind.UserWaypoint
                    : isTransition ? RouteVertexKind.Transition
                    : isEndpoint ? RouteVertexKind.Endpoint
                    : RouteVertexKind.Turn;
                rows.Add(new RouteVertexRow(rows.Count + 1, coordinate, kind));
            }
        }

        return rows;
    }

    private static IReadOnlyList<GeoCoordinate> ExpandWithTransitionPoints(RouteAlternative route)
    {
        if (route.Geometry.Count < 2 || route.TransitionPoints is not { Count: > 0 } transitions)
        {
            return route.Geometry;
        }

        var expanded = new List<GeoCoordinate> { route.Geometry[0] };
        for (int index = 1; index < route.Geometry.Count; index++)
        {
            GeoCoordinate start = route.Geometry[index - 1];
            GeoCoordinate end = route.Geometry[index];
            foreach ((GeoCoordinate Point, (double Fraction, double DistanceMetres) Position) transition in transitions
                .Select(point => (Point: point, Position: PositionOnSegment(start, end, point)))
                .Where(item => item.Position.DistanceMetres <= TransitionOnSegmentToleranceMetres
                    && item.Position.Fraction > 0
                    && item.Position.Fraction < 1)
                .OrderBy(item => item.Position.Fraction))
            {
                if (expanded[^1] != transition.Point)
                {
                    expanded.Add(transition.Point);
                }
            }

            if (expanded[^1] != end)
            {
                expanded.Add(end);
            }
        }

        return expanded;
    }

    private static (double Fraction, double DistanceMetres) PositionOnSegment(
        GeoCoordinate start,
        GeoCoordinate end,
        GeoCoordinate point)
    {
        double latitudeScale = Math.Cos((start.Latitude + end.Latitude) / 2 * Math.PI / 180) * LongitudeMetresPerDegree;
        double dx = (end.Longitude - start.Longitude) * latitudeScale;
        double dy = (end.Latitude - start.Latitude) * LatitudeMetresPerDegree;
        double px = (point.Longitude - start.Longitude) * latitudeScale;
        double py = (point.Latitude - start.Latitude) * LatitudeMetresPerDegree;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared == 0)
        {
            return (0, Math.Sqrt(px * px + py * py));
        }

        double fraction = Math.Clamp((px * dx + py * dy) / lengthSquared, 0, 1);
        double offsetX = px - fraction * dx;
        double offsetY = py - fraction * dy;
        return (fraction, Math.Sqrt(offsetX * offsetX + offsetY * offsetY));
    }

    private static bool IsSignificantTurn(GeoCoordinate previous, GeoCoordinate current, GeoCoordinate next)
    {
        double firstLength = previous.DistanceTo(current);
        double secondLength = current.DistanceTo(next);
        if (firstLength < MinimumTurnSegmentMetres || secondLength < MinimumTurnSegmentMetres)
        {
            return false;
        }

        static double Bearing(GeoCoordinate from, GeoCoordinate to)
        {
            double latitude1 = from.Latitude * Math.PI / 180;
            double latitude2 = to.Latitude * Math.PI / 180;
            double longitudeDelta = (to.Longitude - from.Longitude) * Math.PI / 180;
            return Math.Atan2(
                Math.Sin(longitudeDelta) * Math.Cos(latitude2),
                Math.Cos(latitude1) * Math.Sin(latitude2)
                    - Math.Sin(latitude1) * Math.Cos(latitude2) * Math.Cos(longitudeDelta));
        }

        double difference = Math.Abs(Bearing(previous, current) - Bearing(current, next));
        difference = Math.Min(difference, 2 * Math.PI - difference);
        return difference >= SignificantTurnDegrees * Math.PI / 180;
    }

}
