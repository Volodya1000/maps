using CableRoutePlanner.Domain;

namespace CableRoutePlanner.ViewModels;

/// <summary>Projects route geometry into a compact numbered presentation list.</summary>
internal static class RouteVertexProjection
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private readonly record struct ProjectedTransition(GeoCoordinate Point, double Fraction, double DistanceMetres);

    private const double WaypointCoincidenceToleranceMetres = 1;
    private const double TransitionOnSegmentToleranceMetres = 2;
    // Ignore only sub-metre drawing noise and near-collinear geometry. A short
    // cable bend is still a bend, including the detailed shape of an OSM bridge.
    private const double MinimumTurnSegmentMetres = 0.5;
    private const double SignificantTurnDegrees = 5;
    // Local metric approximation for display only; valid over the Belarus latitude range.
    private const double LongitudeMetresPerDegree = 111_320;
    private const double LatitudeMetresPerDegree = 111_132;

    private sealed record DisplayCandidate(
        int GeometryIndex,
        GeoCoordinate Coordinate,
        RouteVertexKind Kind,
        int WaypointIndex,
        int LegIndex,
        bool IsBridgeTransition);

    // Group only adjacent bridge transitions on the same route leg. This avoids
    // covering the map with multiple OSM versions of one short bridge crossing.
    // Engineering geometry and the measured cable length are never simplified.
    private const double BridgeTransitionClusterGapMetres = 25;
    private const double BridgeTransitionDuplicateMetres = 5;
    private const double BridgeNeighbourTurnSuppressionMetres = 5;

    public static IReadOnlyList<RouteVertexRow> Project(RouteAlternative route, IReadOnlyList<GeoCoordinate> waypoints)
    {
        IReadOnlyList<GeoCoordinate> expandedGeometry = ExpandWithTransitionPoints(route);
        GeoCoordinate[] coordinates = expandedGeometry
            .Where((coordinate, index) => index == 0 || coordinate != expandedGeometry[index - 1])
            .ToArray();
        List<DisplayCandidate> candidates = [];
        int legIndex = 1;
        for (int index = 0; index < coordinates.Length; index++)
        {
            GeoCoordinate coordinate = coordinates[index];
            int waypointIndex = -1;
            for (int waypoint = 0; waypoint < waypoints.Count; waypoint++)
            {
                if (waypoints[waypoint].DistanceTo(coordinate) <= WaypointCoincidenceToleranceMetres)
                {
                    waypointIndex = waypoint;
                    break;
                }
            }

            if (waypointIndex >= 0 && waypointIndex < waypoints.Count - 1)
            {
                legIndex = waypointIndex + 1;
            }

            bool isEndpoint = index == 0 || index == coordinates.Length - 1;
            bool isTransition = route.TransitionPoints?.Any(
                point => point.DistanceTo(coordinate) <= WaypointCoincidenceToleranceMetres) == true;
            bool isTurn = index > 0 && index < coordinates.Length - 1 && IsSignificantTurn(
                coordinates[index - 1], coordinate, coordinates[index + 1]);
            if (isEndpoint || waypointIndex >= 0 || isTransition || isTurn)
            {
                RouteVertexKind kind = waypointIndex >= 0 ? RouteVertexKind.UserWaypoint
                    : isTurn ? RouteVertexKind.Turn
                    : isTransition ? RouteVertexKind.Transition
                    : RouteVertexKind.Endpoint;
                candidates.Add(new DisplayCandidate(
                    index, coordinate, kind, waypointIndex, legIndex, isTransition));
            }
        }

        DisplayCandidate[] display = SimplifyBridgeDisplay(coordinates, candidates);
        List<RouteVertexRow> rows = [];
        int currentLeg = 1;
        int subIndex = 0;
        foreach (DisplayCandidate candidate in display)
        {
            if (candidate.WaypointIndex >= 0)
            {
                currentLeg = Math.Min(candidate.WaypointIndex + 1, Math.Max(1, waypoints.Count - 1));
                subIndex = 0;
                rows.Add(new RouteVertexRow(
                    rows.Count + 1, candidate.Coordinate, candidate.Kind, false,
                    candidate.WaypointIndex + 1));
            }
            else
            {
                if (candidate.LegIndex != currentLeg)
                {
                    currentLeg = candidate.LegIndex;
                    subIndex = 0;
                }

                subIndex++;
                rows.Add(new RouteVertexRow(
                    rows.Count + 1, candidate.Coordinate, candidate.Kind, false,
                    currentLeg, subIndex));
            }
        }

        return rows;
    }

    private static DisplayCandidate[] SimplifyBridgeDisplay(
        IReadOnlyList<GeoCoordinate> geometry,
        IReadOnlyList<DisplayCandidate> candidates)
    {
        double[] alongRoute = new double[geometry.Count];
        for (int index = 1; index < geometry.Count; index++)
        {
            alongRoute[index] = alongRoute[index - 1] + geometry[index - 1].DistanceTo(geometry[index]);
        }

        DisplayCandidate[] crossings = candidates
            .Where(candidate => candidate.IsBridgeTransition && candidate.WaypointIndex < 0)
            .ToArray();
        HashSet<int> suppressed = [];
        int i = 0;
        while (i < crossings.Length)
        {
            int last = i;
            while (last + 1 < crossings.Length
                && crossings[last + 1].LegIndex == crossings[last].LegIndex
                && alongRoute[crossings[last + 1].GeometryIndex]
                    - alongRoute[crossings[last].GeometryIndex] <= BridgeTransitionClusterGapMetres)
            {
                last++;
            }

            DisplayCandidate entry = crossings[i];
            DisplayCandidate exit = crossings[last];
            // Keep entry and exit for one contiguous bridge area, not every
            // duplicate transition or short interior technical turn.
            for (int member = i + 1; member < last; member++)
            {
                suppressed.Add(crossings[member].GeometryIndex);
            }

            if (last > i)
            {
                foreach (DisplayCandidate candidate in candidates)
                {
                    if (candidate.WaypointIndex < 0
                        && candidate.GeometryIndex > entry.GeometryIndex
                        && candidate.GeometryIndex < exit.GeometryIndex
                        && candidate.LegIndex == entry.LegIndex)
                    {
                        suppressed.Add(candidate.GeometryIndex);
                    }
                }

                if (alongRoute[exit.GeometryIndex] - alongRoute[entry.GeometryIndex]
                    < BridgeTransitionDuplicateMetres)
                {
                    suppressed.Add(exit.GeometryIndex);
                }
            }

            i = last + 1;
        }

        // Hide redundant kinks near crossing boundaries so the displayed entry
        // and exit markers remain distinguishable.
        foreach (DisplayCandidate candidate in candidates)
        {
            if (candidate.Kind != RouteVertexKind.Turn || candidate.IsBridgeTransition)
            {
                continue;
            }

            if (crossings.Any(crossing => crossing.LegIndex == candidate.LegIndex
                && Math.Abs(alongRoute[crossing.GeometryIndex] - alongRoute[candidate.GeometryIndex])
                    <= BridgeNeighbourTurnSuppressionMetres))
            {
                suppressed.Add(candidate.GeometryIndex);
            }
        }

        return candidates.Where(candidate => !suppressed.Contains(candidate.GeometryIndex)).ToArray();
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
            foreach (ProjectedTransition transition in transitions
                .Select(point =>
                {
                    (double Fraction, double DistanceMetres) position = PositionOnSegment(start, end, point);
                    return new ProjectedTransition(point, position.Fraction, position.DistanceMetres);
                })
                .Where(item => item.DistanceMetres <= TransitionOnSegmentToleranceMetres
                    && item.Fraction > 0
                    && item.Fraction < 1)
                .OrderBy(item => item.Fraction))
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
