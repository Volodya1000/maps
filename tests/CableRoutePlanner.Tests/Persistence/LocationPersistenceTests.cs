using CableRoutePlanner.Application;
using CableRoutePlanner.Persistence;
using CableRoutePlanner.Domain;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class LocationPersistenceTests
{
    [Fact]
    public void MissingLocationPreferencesDefaultToDisabled()
    {
        using TemporaryDirectory directory = new();
        JsonLocationPreferencesStore store = new(Path.Combine(directory.DirectoryPath, "location.json"));

        Assert.False(store.Load().IsEnabled);
    }

    [Fact]
    public void LocationPreferencesRoundTripWithoutRoutingOptions()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.DirectoryPath, "location.json");
        JsonLocationPreferencesStore store = new(path);

        store.Save(new LocationPreferences(true));

        Assert.True(new JsonLocationPreferencesStore(path).Load().IsEnabled);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void TrackingSessionRoundTripsAndCanBeCleared()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.DirectoryPath, "tracking.json");
        JsonTrackingSessionStore store = new(path);
        GeoCoordinate[] geometry = [new(54, 27), new(54.001, 27)];
        RouteProgressSnapshot snapshot = new(
            Guid.NewGuid(),
            "fingerprint",
            geometry,
            [new ProgressInterval(10, 20)],
            ProgressTrackingStatus.Paused,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        store.Save(snapshot);

        RouteProgressSnapshot? loaded = new JsonTrackingSessionStore(path).Load();
        Assert.NotNull(loaded);
        Assert.Equal(snapshot.SessionId, loaded.SessionId);
        Assert.Equal(snapshot.RouteFingerprint, loaded.RouteFingerprint);
        Assert.Equal(snapshot.Geometry, loaded.Geometry);
        Assert.Equal(snapshot.CoveredIntervals, loaded.CoveredIntervals);
        Assert.Equal(snapshot.Status, loaded.Status);
        store.Clear();
        Assert.Null(store.Load());
    }

    [Fact]
    public void CorruptedTrackingSessionIsReportedAsPlanningFailure()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.DirectoryPath, "tracking.json");
        File.WriteAllText(path, "not-json");

        PlanningException error = Assert.Throws<PlanningException>(() => new JsonTrackingSessionStore(path).Load());

        Assert.Equal(PlanningErrorReason.Unknown, error.Reason);
    }

    [Fact]
    public void InvalidTrackingIntervalsAreRejectedBeforeRestore()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.DirectoryPath, "tracking.json");
        File.WriteAllText(path, $$"""
            {
              "SessionId": "{{Guid.NewGuid()}}",
              "RouteFingerprint": "fingerprint",
              "Geometry": [
                { "Latitude": 54.0, "Longitude": 27.0 },
                { "Latitude": 54.001, "Longitude": 27.0 }
              ],
              "CoveredIntervals": [ { "StartMetres": 0, "EndMetres": 999999 } ],
              "Status": 2,
              "StartedUtc": "2026-11-04T12:00:00+00:00",
              "UpdatedUtc": "2026-11-04T12:01:00+00:00"
            }
            """);

        Assert.Throws<PlanningException>(() => new JsonTrackingSessionStore(path).Load());
    }

    [Fact]
    public void FullyCoveredRouteRoundTripsAtExactlyOneHundredPercent()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.DirectoryPath, "tracking.json");
        GeoCoordinate[] geometry = [new(53.9, 27.4), new(53.9, 27.5)];
        RouteAlternative route = new(
            Guid.NewGuid(),
            "Длинный прямой участок",
            geometry,
            new RouteMetrics(6_500, 0, 6_500, 0, 0, 0, 6_500, null));
        DateTimeOffset start = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        RouteProgressTracker tracker = new(new RouteProgressConfiguration(
            MaxSpeedMetresPerSecond: 200,
            MaxConsecutiveGap: TimeSpan.FromMinutes(10)));
        tracker.Begin(route);
        tracker.Resume();
        tracker.Accept(new LocationMeasurement(geometry[0], 1, start), start);
        RouteProgressState state = tracker.Accept(
            new LocationMeasurement(geometry[1], 1, start.AddMinutes(1)), start.AddMinutes(1));
        RouteProgressSnapshot snapshot = tracker.CreateSnapshot(Guid.NewGuid(), start, start.AddMinutes(1));

        Assert.Equal(100, state.CoveragePercent, 6);

        JsonTrackingSessionStore store = new(path);
        store.Save(snapshot);
        RouteProgressSnapshot? loaded = new JsonTrackingSessionStore(path).Load();

        Assert.NotNull(loaded);
        Assert.Equal(snapshot.CoveredIntervals, loaded.CoveredIntervals);
    }
}
