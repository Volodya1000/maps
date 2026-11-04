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
}
