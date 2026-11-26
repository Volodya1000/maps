using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;

namespace CableRoutePlanner.Infrastructure.Elevation;

internal sealed class RouteElevationAnalyzer(IElevationProvider provider) : IRouteElevationAnalyzer
{
    private const double MaximumSamplingIntervalMetres = 30;
    private const int MaximumSamples = 100_000;
    private const double MinimumGradeWindowRadiusMetres = 15;

    public async Task<RouteAlternative> AnalyzeAsync(RouteAlternative route, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);
        cancellationToken.ThrowIfCancellationRequested();
        ElevationSource? source = provider.Source;
        List<(GeoCoordinate Coordinate, double DistanceMetres)>? stations =
            source is null ? null : BuildStations(route.Geometry, source.ResolutionMetres, cancellationToken);

        if (source is null || stations is null)
        {
            double horizontalLength = MeasureHorizontalLength(route.Geometry, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return route.WithElevationSamples(source, [], horizontalLength);
        }

        GeoCoordinate[] coordinates = stations.Select(station => station.Coordinate).ToArray();
        IReadOnlyList<double?> heights = await provider.ReadAsync(coordinates, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (heights.Count != stations.Count)
        {
            throw new InvalidOperationException(
                $"DEM вернула {heights.Count} высот для {stations.Count} точек маршрута.");
        }

        double?[] grades = CalculateGrades(stations, heights, source.ResolutionMetres, cancellationToken);
        RouteElevationSample[] samples = new RouteElevationSample[stations.Count];
        for (int index = 0; index < samples.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (GeoCoordinate coordinate, double distance) = stations[index];
            samples[index] = new(coordinate, distance, heights[index], grades[index]);
        }

        RouteAlternative enriched = route.WithElevationSamples(source, samples, stations[^1].DistanceMetres);
        cancellationToken.ThrowIfCancellationRequested();
        return enriched;
    }

    private static List<(GeoCoordinate Coordinate, double DistanceMetres)>? BuildStations(
        IReadOnlyList<GeoCoordinate> geometry, double resolutionMetres, CancellationToken cancellationToken)
    {
        double interval = Math.Min(MaximumSamplingIntervalMetres, resolutionMetres / 2);
        if (interval <= 0 || geometry.Count == 0 || geometry.Count > MaximumSamples)
        {
            return null;
        }

        List<(GeoCoordinate Coordinate, double DistanceMetres)> stations = [(geometry[0], 0)];
        for (int index = 1; index < geometry.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GeoCoordinate start = geometry[index - 1];
            GeoCoordinate end = geometry[index];
            double requiredIntervals = Math.Ceiling(Wgs84Polyline.SegmentLengthUpperBound(start, end) / interval);
            if (requiredIntervals > MaximumSamples - stations.Count)
            {
                return null;
            }

            int segments = (int)requiredIntervals;
            for (int step = 1; step <= segments; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GeoCoordinate coordinate = step == segments ? end : Wgs84Polyline.Interpolate(start, end, (double)step / segments);
                (GeoCoordinate previous, double previousDistance) = stations[^1];
                double distance = previousDistance + Wgs84Polyline.SegmentLength(previous, coordinate);
                if (distance > previousDistance)
                {
                    stations.Add((coordinate, distance));
                }
            }
        }

        return stations;
    }

    private static double MeasureHorizontalLength(IReadOnlyList<GeoCoordinate> geometry, CancellationToken cancellationToken)
    {
        double length = 0;
        for (int index = 1; index < geometry.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            length += Wgs84Polyline.SegmentLength(geometry[index - 1], geometry[index]);
        }

        return length;
    }

    private static double?[] CalculateGrades(List<(GeoCoordinate Coordinate, double DistanceMetres)> stations,
        IReadOnlyList<double?> heights, double resolutionMetres, CancellationToken cancellationToken)
    {
        double radius = Math.Max(MinimumGradeWindowRadiusMetres, resolutionMetres);
        double?[] grades = new double?[heights.Count];
        // Clamp the display window to each continuous run of known heights.
        // Its smoothing never enters the surface-length calculation.
        int first = 0;
        while (first < heights.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!heights[first].HasValue)
            {
                first++;
                continue;
            }

            int last = first;
            while (last + 1 < heights.Count && heights[last + 1].HasValue)
            {
                last++;
            }

            if (last > first)
            {
                CalculateRunGrades(stations, heights, grades, first, last, radius, cancellationToken);
            }

            first = last + 1;
        }

        return grades;
    }

    private static void CalculateRunGrades(List<(GeoCoordinate Coordinate, double DistanceMetres)> stations,
        IReadOnlyList<double?> heights, double?[] grades, int first, int last, double radius,
        CancellationToken cancellationToken)
    {
        for (int index = first; index <= last; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double station = stations[index].DistanceMetres;
            double left = Math.Max(stations[first].DistanceMetres, station - radius);
            double right = Math.Min(stations[last].DistanceMetres, station + radius);
            double leftHeight = InterpolateHeight(stations, heights, first, last, left);
            double rightHeight = InterpolateHeight(stations, heights, first, last, right);
            grades[index] = (rightHeight - leftHeight) / (right - left) * 100;
        }
    }

    private static double InterpolateHeight(List<(GeoCoordinate Coordinate, double DistanceMetres)> stations,
        IReadOnlyList<double?> heights, int first, int last, double distance)
    {
        while (last - first > 1)
        {
            int middle = first + (last - first) / 2;
            if (stations[middle].DistanceMetres <= distance)
            {
                first = middle;
            }
            else
            {
                last = middle;
            }
        }

        double start = stations[first].DistanceMetres;
        double fraction = (distance - start) / (stations[last].DistanceMetres - start);
        double elevation = heights[first]!.Value;
        return elevation + (heights[last]!.Value - elevation) * fraction;
    }
}
