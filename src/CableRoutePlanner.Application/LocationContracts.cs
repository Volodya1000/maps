using CableRoutePlanner.Domain;
using System.Runtime.InteropServices;

namespace CableRoutePlanner.Application;

public sealed record LocationMeasurement(
    GeoCoordinate Coordinate,
    double AccuracyMetres,
    DateTimeOffset TimestampUtc,
    double? SpeedMetresPerSecond = null,
    bool IsApproximate = false);

public sealed record HeadingMeasurement(
    double Degrees,
    bool IsReliable,
    DateTimeOffset TimestampUtc);

public sealed record LocationPreferences(bool IsEnabled = false);

public enum LocationPermissionState
{
    NotRequested,
    Fine,
    Approximate,
    Denied,
    PermanentlyDenied
}

public enum SystemLocationState
{
    Unknown,
    Enabled,
    Disabled
}

public sealed record LocationServiceState(
    bool IsEnabled,
    LocationPermissionState Permission,
    SystemLocationState SystemLocation,
    bool IsSearching,
    string? Message = null)
{
    public static LocationServiceState Disabled { get; } = new(
        false,
        LocationPermissionState.NotRequested,
        SystemLocationState.Unknown,
        false);

    public static LocationServiceState Enabled { get; } = new(
        true,
        LocationPermissionState.Fine,
        SystemLocationState.Enabled,
        false);
}

public interface ILocationService : IDisposable
{
    bool IsSupported { get; }

    LocationServiceState State { get; }

    IObservable<LocationMeasurement> Measurements { get; }

    IObservable<LocationServiceState> States { get; }

    Task<LocationServiceState> EnableAsync(CancellationToken cancellationToken);

    void Disable();

    void PauseUpdates();

    void ResumeUpdates();

    void OpenSystemSettings();
}

public interface IHeadingService : IDisposable
{
    bool IsSupported { get; }

    IObservable<HeadingMeasurement> Measurements { get; }

    void PauseUpdates();

    void ResumeUpdates();
}

public interface ILocationPreferencesStore
{
    LocationPreferences Load();

    void Save(LocationPreferences preferences);
}

public interface ITrackingSessionStore
{
    RouteProgressSnapshot? Load();

    void Save(RouteProgressSnapshot snapshot);

    void Clear();
}

public enum LocationQuality
{
    Unknown,
    Good,
    Approximate,
    Stale,
    Invalid
}

public enum ProgressTrackingStatus
{
    Ready,
    Active,
    Paused,
    Completed
}

[StructLayout(LayoutKind.Sequential)]
public readonly record struct ProgressInterval(double StartMetres, double EndMetres)
{
    public double LengthMetres => Math.Max(0, EndMetres - StartMetres);
}

public sealed record RouteProgressConfiguration(
    double MaxAccuracyMetres = 50,
    TimeSpan MaxMeasurementAge = default,
    TimeSpan MaxConsecutiveGap = default,
    double MaxSpeedMetresPerSecond = 20,
    double ProjectionToleranceMetres = 60,
    double StationaryDistanceMetres = 3)
{
    public TimeSpan EffectiveMaxMeasurementAge => MaxMeasurementAge == default
        ? TimeSpan.FromMinutes(2) : MaxMeasurementAge;

    public TimeSpan EffectiveMaxConsecutiveGap => MaxConsecutiveGap == default
        ? TimeSpan.FromMinutes(2) : MaxConsecutiveGap;
}

public sealed record RouteProgressState(
    string RouteFingerprint,
    double TotalDistanceMetres,
    IReadOnlyList<ProgressInterval> CoveredIntervals,
    double CoveredDistanceMetres,
    double RemainingDistanceMetres,
    double CoveragePercent,
    IReadOnlyList<IReadOnlyList<GeoCoordinate>> CoveredGeometry,
    ProgressTrackingStatus Status,
    LocationQuality LocationQuality);

public sealed record RouteProgressSnapshot(
    Guid SessionId,
    string RouteFingerprint,
    IReadOnlyList<GeoCoordinate> Geometry,
    IReadOnlyList<ProgressInterval> CoveredIntervals,
    ProgressTrackingStatus Status,
    DateTimeOffset StartedUtc,
    DateTimeOffset? UpdatedUtc);

public sealed class HeadingSmoother
{
    private readonly double _smoothingFactor;
    private double? _lastHeading;

    public HeadingSmoother(double smoothingFactor)
    {
        if (!double.IsFinite(smoothingFactor) || smoothingFactor is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(smoothingFactor));
        }

        _smoothingFactor = smoothingFactor;
    }

    public double? Update(double headingDegrees, bool reliable)
    {
        if (!reliable || !double.IsFinite(headingDegrees))
        {
            return null;
        }

        double normalized = Normalize(headingDegrees);
        if (_lastHeading is not { } previous)
        {
            _lastHeading = normalized;
            return normalized;
        }

        double delta = ShortestDelta(previous, normalized);
        double smoothed = Normalize(previous + delta * _smoothingFactor);
        _lastHeading = smoothed;
        return smoothed;
    }

    public static double ToMapBearing(double headingDegrees, double mapRotationDegrees, double displayRotationDegrees) =>
        Normalize(headingDegrees - mapRotationDegrees - displayRotationDegrees);

    public static double Normalize(double degrees)
    {
        double result = degrees % 360;
        return result < 0 ? result + 360 : result;
    }

    private static double ShortestDelta(double from, double to)
    {
        double delta = Normalize(to - from);
        return delta > 180 ? delta - 360 : delta;
    }
}
