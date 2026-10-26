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
        var expandedGeometry = ExpandWithTransitionPoints(route);
        var coordinates = expandedGeometry
            .Where((coordinate, index) => index == 0 || coordinate != expandedGeometry[index - 1])
            .ToArray();
        for (var index = 0; index < coordinates.Length; index++)
        {
            var coordinate = coordinates[index];
            var isEndpoint = index == 0 || index == coordinates.Length - 1;
            var isWaypoint = waypoints.Any(point => point.DistanceTo(coordinate) <= WaypointCoincidenceToleranceMetres);
            var isTransition = route.TransitionPoints?.Any(point => point.DistanceTo(coordinate) <= WaypointCoincidenceToleranceMetres) == true;
            var isTurn = index > 0 && index < coordinates.Length - 1 && IsSignificantTurn(
                coordinates[index - 1], coordinate, coordinates[index + 1]);
            if ((isEndpoint || isWaypoint || isTransition || isTurn)
                && (rows.Count == 0 || rows[^1].Coordinate != coordinate))
            {
                var kind = isWaypoint ? RouteVertexKind.UserWaypoint
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
        for (var index = 1; index < route.Geometry.Count; index++)
        {
            var start = route.Geometry[index - 1];
            var end = route.Geometry[index];
            foreach (var transition in transitions
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
        var latitudeScale = Math.Cos((start.Latitude + end.Latitude) / 2 * Math.PI / 180) * LongitudeMetresPerDegree;
        var dx = (end.Longitude - start.Longitude) * latitudeScale;
        var dy = (end.Latitude - start.Latitude) * LatitudeMetresPerDegree;
        var px = (point.Longitude - start.Longitude) * latitudeScale;
        var py = (point.Latitude - start.Latitude) * LatitudeMetresPerDegree;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared == 0)
        {
            return (0, Math.Sqrt(px * px + py * py));
        }

        var fraction = Math.Clamp((px * dx + py * dy) / lengthSquared, 0, 1);
        var offsetX = px - fraction * dx;
        var offsetY = py - fraction * dy;
        return (fraction, Math.Sqrt(offsetX * offsetX + offsetY * offsetY));
    }

    private static bool IsSignificantTurn(GeoCoordinate previous, GeoCoordinate current, GeoCoordinate next)
    {
        var firstLength = previous.DistanceTo(current);
        var secondLength = current.DistanceTo(next);
        if (firstLength < MinimumTurnSegmentMetres || secondLength < MinimumTurnSegmentMetres)
        {
            return false;
        }

        static double Bearing(GeoCoordinate from, GeoCoordinate to)
        {
            var latitude1 = from.Latitude * Math.PI / 180;
            var latitude2 = to.Latitude * Math.PI / 180;
            var longitudeDelta = (to.Longitude - from.Longitude) * Math.PI / 180;
            return Math.Atan2(
                Math.Sin(longitudeDelta) * Math.Cos(latitude2),
                Math.Cos(latitude1) * Math.Sin(latitude2)
                    - Math.Sin(latitude1) * Math.Cos(latitude2) * Math.Cos(longitudeDelta));
        }

        var difference = Math.Abs(Bearing(previous, current) - Bearing(current, next));
        difference = Math.Min(difference, 2 * Math.PI - difference);
        return difference >= SignificantTurnDegrees * Math.PI / 180;
    }

}
