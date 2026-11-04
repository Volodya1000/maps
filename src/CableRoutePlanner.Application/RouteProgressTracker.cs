using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using CableRoutePlanner.Domain;

namespace CableRoutePlanner.Application;

public sealed class RouteProgressTracker
{
    private readonly RouteProgressConfiguration _configuration;
    private readonly List<ProgressInterval> _coveredIntervals = [];
    private RouteAlternative? _route;
    private Segment[] _segments = [];
    private double[] _cumulativeLengths = [];
    private LocationMeasurement? _previousMeasurement;
    private Projection? _previousProjection;
    private ProgressTrackingStatus _status = ProgressTrackingStatus.Ready;
    private LocationQuality _locationQuality = LocationQuality.Unknown;

    public RouteProgressTracker(RouteProgressConfiguration? configuration = null)
    {
        _configuration = configuration ?? new RouteProgressConfiguration();
        ValidateConfiguration(_configuration);
    }

    public RouteProgressState Begin(RouteAlternative route)
    {
        ArgumentNullException.ThrowIfNull(route);
        _route = route;
        (Segment[] segments, double[] cumulativeLengths) = BuildSegments(route.Geometry);
        _segments = segments;
        _cumulativeLengths = cumulativeLengths;
        _coveredIntervals.Clear();
        _previousMeasurement = null;
        _previousProjection = null;
        _status = ProgressTrackingStatus.Active;
        _locationQuality = LocationQuality.Unknown;
        return BuildState();
    }

    public RouteProgressState Restore(RouteProgressSnapshot snapshot, RouteAlternative route)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RouteProgressState state = Begin(route);
        if (!string.Equals(snapshot.RouteFingerprint, state.RouteFingerprint, StringComparison.Ordinal)
            || !GeometryEquals(snapshot.Geometry, route.Geometry))
        {
            return state;
        }

        _coveredIntervals.AddRange(NormalizeIntervals(snapshot.CoveredIntervals));
        // A process restart is a tracking gap. Even if the last atomic snapshot was
        // written while active, the user must explicitly resume before a new pair of
        // fixes can contribute to progress.
        _status = snapshot.Status == ProgressTrackingStatus.Active
            ? ProgressTrackingStatus.Paused
            : snapshot.Status;
        return BuildState();
    }

    public RouteProgressState Pause()
    {
        _status = ProgressTrackingStatus.Paused;
        _previousMeasurement = null;
        _previousProjection = null;
        return BuildState();
    }

    public RouteProgressState Resume()
    {
        if (_status != ProgressTrackingStatus.Completed)
        {
            _status = ProgressTrackingStatus.Active;
            _previousMeasurement = null;
            _previousProjection = null;
        }

        return BuildState();
    }

    public RouteProgressState Finish()
    {
        _status = ProgressTrackingStatus.Completed;
        _previousMeasurement = null;
        _previousProjection = null;
        return BuildState();
    }

    public RouteProgressState Accept(LocationMeasurement measurement, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        if (_route is null || _status != ProgressTrackingStatus.Active)
        {
            return BuildState();
        }

        if (!IsCredible(measurement, nowUtc, out LocationQuality quality))
        {
            _locationQuality = quality;
            _previousMeasurement = null;
            _previousProjection = null;
            return BuildState();
        }

        Projection? projection = FindProjection(measurement.Coordinate);
        if (projection is null)
        {
            _locationQuality = LocationQuality.Invalid;
            _previousMeasurement = null;
            _previousProjection = null;
            return BuildState();
        }

        _locationQuality = measurement.IsApproximate ? LocationQuality.Approximate : LocationQuality.Good;
        if (_previousMeasurement is not { } previousMeasurement || _previousProjection is not { } previousProjection)
        {
            _previousMeasurement = measurement;
            _previousProjection = projection;
            return BuildState();
        }

        TimeSpan elapsed = measurement.TimestampUtc - previousMeasurement.TimestampUtc;
        if (elapsed <= TimeSpan.Zero || elapsed > _configuration.EffectiveMaxConsecutiveGap)
        {
            _previousMeasurement = measurement;
            _previousProjection = projection;
            return BuildState();
        }

        double routeDistance = Math.Abs(projection.PositionMetres - previousProjection.PositionMetres);
        double allowedDistance = _configuration.MaxSpeedMetresPerSecond * elapsed.TotalSeconds
            + measurement.AccuracyMetres + previousMeasurement.AccuracyMetres;
        if (routeDistance > Math.Max(allowedDistance, _configuration.StationaryDistanceMetres))
        {
            _previousMeasurement = measurement;
            _previousProjection = projection;
            _locationQuality = LocationQuality.Invalid;
            return BuildState();
        }

        if (routeDistance >= _configuration.StationaryDistanceMetres)
        {
            AddInterval(new ProgressInterval(
                Math.Min(previousProjection.PositionMetres, projection.PositionMetres),
                Math.Max(previousProjection.PositionMetres, projection.PositionMetres)));
        }

        _previousMeasurement = measurement;
        _previousProjection = projection;
        return BuildState();
    }

    public RouteProgressSnapshot CreateSnapshot(Guid sessionId, DateTimeOffset startedUtc, DateTimeOffset? updatedUtc)
    {
        RouteProgressState state = BuildState();
        IReadOnlyList<GeoCoordinate> geometry = _route?.Geometry.ToArray() ?? [];
        return new RouteProgressSnapshot(
            sessionId,
            state.RouteFingerprint,
            geometry,
            state.CoveredIntervals,
            state.Status,
            startedUtc,
            updatedUtc);
    }

    private RouteProgressState BuildState()
    {
        string fingerprint = _route is null ? "" : Fingerprint(_route.Geometry);
        double total = _cumulativeLengths.Length == 0 ? 0 : _cumulativeLengths[^1];
        IReadOnlyList<ProgressInterval> intervals = NormalizeIntervals(_coveredIntervals);
        double covered = intervals.Sum(interval => interval.LengthMetres);
        double remaining = Math.Max(0, total - covered);
        double percent = total <= 0 ? 0 : covered / total * 100;
        return new RouteProgressState(
            fingerprint,
            total,
            intervals,
            covered,
            remaining,
            percent,
            BuildCoveredGeometry(intervals),
            _status,
            _locationQuality);
    }

    private bool IsCredible(LocationMeasurement measurement, DateTimeOffset nowUtc, out LocationQuality quality)
    {
        quality = LocationQuality.Invalid;
        if (!double.IsFinite(measurement.AccuracyMetres)
            || measurement.AccuracyMetres < 0
            || measurement.AccuracyMetres > _configuration.MaxAccuracyMetres)
        {
            return false;
        }

        TimeSpan age = nowUtc - measurement.TimestampUtc;
        if (age < TimeSpan.Zero || age > _configuration.EffectiveMaxMeasurementAge)
        {
            quality = age > TimeSpan.Zero ? LocationQuality.Stale : LocationQuality.Invalid;
            return false;
        }

        if (measurement.SpeedMetresPerSecond is { } speed
            && (!double.IsFinite(speed) || speed < 0 || speed > _configuration.MaxSpeedMetresPerSecond))
        {
            return false;
        }

        quality = measurement.IsApproximate ? LocationQuality.Approximate : LocationQuality.Good;
        return true;
    }

    private Projection? FindProjection(GeoCoordinate coordinate)
    {
        if (_segments.Length == 0)
        {
            return null;
        }

        double latitude = _segments[0].Start.Latitude;
        LocalPoint point = ToLocal(coordinate, latitude);
        List<Projection> candidates = [];
        foreach (Segment segment in _segments)
        {
            double dx = segment.EndLocal.X - segment.StartLocal.X;
            double dy = segment.EndLocal.Y - segment.StartLocal.Y;
            double lengthSquared = dx * dx + dy * dy;
            double fraction = lengthSquared <= 0
                ? 0
                : Math.Clamp(((point.X - segment.StartLocal.X) * dx + (point.Y - segment.StartLocal.Y) * dy)
                    / lengthSquared, 0, 1);
            LocalPoint projected = new(
                segment.StartLocal.X + dx * fraction,
                segment.StartLocal.Y + dy * fraction);
            double distance = Distance(point, projected);
            if (distance <= _configuration.ProjectionToleranceMetres)
            {
                candidates.Add(new Projection(
                    segment.StartDistanceMetres + segment.LengthMetres * fraction,
                    distance,
                    segment.Index));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        if (_previousProjection is not { } previous)
        {
            return candidates.OrderBy(candidate => candidate.DistanceMetres).First();
        }

        return candidates
            .OrderBy(candidate => candidate.DistanceMetres
                + Math.Abs(candidate.PositionMetres - previous.PositionMetres) * 0.01)
            .First();
    }

    private void AddInterval(ProgressInterval interval)
    {
        if (interval.EndMetres <= interval.StartMetres)
        {
            return;
        }

        _coveredIntervals.Add(interval);
        List<ProgressInterval> normalized = NormalizeIntervals(_coveredIntervals).ToList();
        _coveredIntervals.Clear();
        _coveredIntervals.AddRange(normalized);
    }

    private List<IReadOnlyList<GeoCoordinate>> BuildCoveredGeometry(
        IReadOnlyList<ProgressInterval> intervals)
    {
        List<IReadOnlyList<GeoCoordinate>> geometry = [];
        foreach (ProgressInterval interval in intervals)
        {
            List<GeoCoordinate> points = [CoordinateAt(interval.StartMetres)];
            for (int index = 1; index < _cumulativeLengths.Length - 1; index++)
            {
                if (_cumulativeLengths[index] > interval.StartMetres
                    && _cumulativeLengths[index] < interval.EndMetres)
                {
                    points.Add(_segments[index].Start);
                }
            }

            GeoCoordinate end = CoordinateAt(interval.EndMetres);
            if (points[^1] != end)
            {
                points.Add(end);
            }

            geometry.Add(points);
        }

        return geometry;
    }

    private GeoCoordinate CoordinateAt(double positionMetres)
    {
        if (_segments.Length == 0)
        {
            return _route?.Geometry is { Count: > 0 } geometry ? geometry[0] : new GeoCoordinate(0, 0);
        }

        double position = Math.Clamp(positionMetres, 0, _cumulativeLengths[^1]);
        Segment segment = _segments[^1];
        foreach (Segment candidate in _segments)
        {
            if (position <= candidate.StartDistanceMetres + candidate.LengthMetres)
            {
                segment = candidate;
                break;
            }
        }

        double fraction = segment.LengthMetres <= 0
            ? 0
            : (position - segment.StartDistanceMetres) / segment.LengthMetres;
        return new(
            segment.Start.Latitude + (segment.End.Latitude - segment.Start.Latitude) * fraction,
            segment.Start.Longitude + (segment.End.Longitude - segment.Start.Longitude) * fraction);
    }

    private static (Segment[] Segments, double[] CumulativeLengths) BuildSegments(
        IReadOnlyList<GeoCoordinate> geometry)
    {
        if (geometry.Count < 2)
        {
            return ([], [0]);
        }

        List<Segment> segments = [];
        List<double> cumulative = [0];
        double total = 0;
        double latitude = geometry[0].Latitude;
        LocalPoint previous = ToLocal(geometry[0], latitude);
        for (int index = 0; index < geometry.Count - 1; index++)
        {
            GeoCoordinate start = geometry[index];
            GeoCoordinate end = geometry[index + 1];
            LocalPoint startLocal = index == 0 ? previous : ToLocal(start, latitude);
            LocalPoint endLocal = ToLocal(end, latitude);
            double length = Distance(startLocal, endLocal);
            if (length <= 0.001)
            {
                continue;
            }

            segments.Add(new Segment(index, start, end, startLocal, endLocal, total, length));
            total += length;
            cumulative.Add(total);
        }

        return (segments.ToArray(), cumulative.ToArray());
    }

    private static List<ProgressInterval> NormalizeIntervals(
        IEnumerable<ProgressInterval> intervals)
    {
        List<ProgressInterval> sorted = intervals
            .Where(interval => double.IsFinite(interval.StartMetres)
                && double.IsFinite(interval.EndMetres)
                && interval.EndMetres > interval.StartMetres)
            .OrderBy(interval => interval.StartMetres)
            .ToList();
        if (sorted.Count == 0)
        {
            return [];
        }

        List<ProgressInterval> result = [sorted[0]];
        foreach (ProgressInterval interval in sorted.Skip(1))
        {
            ProgressInterval current = result[^1];
            if (interval.StartMetres <= current.EndMetres + 0.001)
            {
                result[^1] = new ProgressInterval(current.StartMetres, Math.Max(current.EndMetres, interval.EndMetres));
            }
            else
            {
                result.Add(interval);
            }
        }

        return result;
    }

    private static bool GeometryEquals(IReadOnlyList<GeoCoordinate> left, IReadOnlyList<GeoCoordinate> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First == pair.Second);

    private static string Fingerprint(IReadOnlyList<GeoCoordinate> geometry)
    {
        StringBuilder text = new();
        foreach (GeoCoordinate coordinate in geometry)
        {
            text.Append(coordinate.Latitude.ToString("R", CultureInfo.InvariantCulture));
            text.Append(',');
            text.Append(coordinate.Longitude.ToString("R", CultureInfo.InvariantCulture));
            text.Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static LocalPoint ToLocal(GeoCoordinate coordinate, double referenceLatitude)
    {
        const double radians = Math.PI / 180;
        double longitudeScale = GeoCoordinate.MeanEarthRadiusMetres * Math.Cos(referenceLatitude * radians) * radians;
        double latitudeScale = GeoCoordinate.MeanEarthRadiusMetres * radians;
        return new(coordinate.Longitude * longitudeScale, coordinate.Latitude * latitudeScale);
    }

    private static double Distance(LocalPoint first, LocalPoint second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));

    private static void ValidateConfiguration(RouteProgressConfiguration configuration)
    {
        if (!double.IsFinite(configuration.MaxAccuracyMetres) || configuration.MaxAccuracyMetres <= 0
            || !double.IsFinite(configuration.MaxSpeedMetresPerSecond) || configuration.MaxSpeedMetresPerSecond <= 0
            || !double.IsFinite(configuration.ProjectionToleranceMetres) || configuration.ProjectionToleranceMetres <= 0
            || !double.IsFinite(configuration.StationaryDistanceMetres) || configuration.StationaryDistanceMetres < 0
            || configuration.EffectiveMaxMeasurementAge <= TimeSpan.Zero
            || configuration.EffectiveMaxConsecutiveGap <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct LocalPoint(double X, double Y);

    private sealed record Segment(
        int Index,
        GeoCoordinate Start,
        GeoCoordinate End,
        LocalPoint StartLocal,
        LocalPoint EndLocal,
        double StartDistanceMetres,
        double LengthMetres);

    private sealed record Projection(double PositionMetres, double DistanceMetres, int SegmentIndex);
}
