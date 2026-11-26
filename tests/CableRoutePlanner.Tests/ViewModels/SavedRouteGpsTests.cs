using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Persistence;
using CableRoutePlanner.UI;
using CableRoutePlanner.ViewModels;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class SavedRouteGpsTests : ReactiveTestScope
{
    [Fact]
    public async Task ColdOpenUsesExistingGpsProgressPauseAndExplicitResumeWithoutTransferringToAnotherGeometry()
    {
        using SavedRouteDirectory directory = new();
        SavedRoute saved = SavedRouteDirectory.Snapshot();
        directory.Store.Add(saved);
        string trackingPath = Path.Combine(directory.DirectoryPath, "tracking-session.json");
        string preferencesPath = Path.Combine(directory.DirectoryPath, "location-settings.json");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        double covered;
        using (GpsSource gps = new())
        using (LocationController controller = new(gps, new DisabledHeadingService(), new JsonLocationPreferencesStore(preferencesPath)))
        using (TrackingViewModel tracking = new(controller, new JsonTrackingSessionStore(trackingPath), () => now))
        {
            NavigationHost screen = new();
            using MapViewModel map = SavedRoutesTests.CreateMap(directory, new SavedRoutesTests.RejectPlanner(), screen, tracking: tracking);
            using SavedRoutesViewModel list = new(screen, map, directory.Store);
            using SettingsViewModel settings = new(screen, new MemorySettings());
            using ShellViewModel shell = new(screen, map, settings, list);
            await shell.ShowSaved.Execute().ToTask();
            await list.Open.Execute(saved.Id).ToTask();
            Assert.Equal(ProgressTrackingStatus.Ready, tracking.Progress!.Status);
            Assert.False(tracking.IsLocationEnabled);
            await tracking.Start.Execute().ToTask();
            Assert.Equal(ProgressTrackingStatus.Ready, tracking.Progress.Status);
            Assert.True(tracking.HasLocationStartMessage);
            // Exercise either saved alternative through the usual route switcher.
            await map.ChooseRoute.Execute(saved.Alternatives[0].Id).ToTask();
            await tracking.SetLocationEnabledAsync(true);
            await tracking.Start.Execute().ToTask();
            gps.Emit(new(TestData.P(20, 10), 1, now));
            now = now.AddSeconds(10);
            gps.Emit(new(TestData.P(30, 10), 1, now));
            covered = tracking.Progress!.CoveredDistanceMetres;
            Assert.True(covered > 9);
            Assert.True(tracking.Progress.CoveragePercent > 0);
            Assert.NotEmpty(tracking.Progress.CoveredGeometry);
            RouteProgressState progress = tracking.Progress;
            await shell.ShowSaved.Execute().ToTask();
            await shell.ShowMap.Execute().ToTask();
            Assert.Same(progress, tracking.Progress);
            Assert.Equal(ProgressTrackingStatus.Active, tracking.Progress.Status);
            await tracking.Pause.Execute().ToTask();
            now = now.AddSeconds(10);
            gps.Emit(new(TestData.P(40, 10), 1, now));
            Assert.Equal(covered, tracking.Progress.CoveredDistanceMetres);
        }
        using GpsSource restoredGps = new();
        using LocationController restoredController = new(restoredGps, new DisabledHeadingService(), new JsonLocationPreferencesStore(preferencesPath));
        using TrackingViewModel restoredTracking = new(restoredController, new JsonTrackingSessionStore(trackingPath), () => now);
        using MapViewModel restoredMap = SavedRoutesTests.CreateMap(directory, new SavedRoutesTests.RejectPlanner(), tracking: restoredTracking);
        Assert.True(restoredMap.OpenSavedRoute(saved.Id));
        await restoredMap.ChooseRoute.Execute(saved.Alternatives[0].Id).ToTask();
        Assert.Equal(ProgressTrackingStatus.Paused, restoredTracking.Progress!.Status);
        Assert.Equal(covered, restoredTracking.Progress.CoveredDistanceMetres);
        now = now.AddSeconds(10);
        restoredGps.Emit(new(TestData.P(50, 10), 1, now));
        Assert.Equal(covered, restoredTracking.Progress.CoveredDistanceMetres);
        await restoredTracking.Resume.Execute().ToTask();
        restoredGps.Emit(new(TestData.P(50, 10), 1, now));
        now = now.AddSeconds(10);
        restoredGps.Emit(new(TestData.P(60, 10), 1, now));
        Assert.True(restoredTracking.Progress.CoveredDistanceMetres > covered);
        SavedRoute sameIdDifferentGeometry = saved with
        {
            Id = Guid.NewGuid(),
            Alternatives = [saved.Alternatives[0] with { Geometry = saved.Alternatives[1].Geometry }],
            SelectedAlternativeId = saved.Alternatives[0].Id
        };
        directory.Store.Add(sameIdDifferentGeometry);
        Assert.True(restoredMap.OpenSavedRoute(sameIdDifferentGeometry.Id));
        Assert.Equal(0, restoredTracking.Progress!.CoveredDistanceMetres);
        Assert.Equal(ProgressTrackingStatus.Ready, restoredTracking.Progress.Status);
        Assert.True(restoredMap.OpenSavedRoute(saved.Id));
        await restoredMap.ChooseRoute.Execute(saved.Alternatives[1].Id).ToTask();
        Assert.Equal(0, restoredTracking.Progress!.CoveredDistanceMetres);
        Assert.Equal(ProgressTrackingStatus.Ready, restoredTracking.Progress.Status);
        await restoredTracking.Start.Execute().ToTask();
        GeoCoordinate start = saved.Alternatives[1].Geometry[0];
        restoredGps.Emit(new(start, 1, now));
        now = now.AddSeconds(10);
        GeoCoordinate next = new(start.Latitude + (saved.Alternatives[1].Geometry[1].Latitude - start.Latitude) / 10,
            start.Longitude + (saved.Alternatives[1].Geometry[1].Longitude - start.Longitude) / 10);
        restoredGps.Emit(new(next, 1, now));
        Assert.True(restoredTracking.Progress.CoveredDistanceMetres > 0);
    }

    private sealed class GpsSource : ILocationService
    {
        private readonly Subject<LocationMeasurement> _fixes = new();
        public bool IsSupported => true;
        public LocationServiceState State { get; private set; } = LocationServiceState.Disabled;
        public IObservable<LocationMeasurement> Measurements => _fixes;
        public IObservable<LocationServiceState> States => Observable.Never<LocationServiceState>();
        public Task<LocationServiceState> EnableAsync(CancellationToken cancellationToken)
        {
            State = LocationServiceState.Enabled;
            return Task.FromResult(State);
        }
        public Task<LocationServiceState> RestoreAsync(CancellationToken cancellationToken) => EnableAsync(cancellationToken);
        public void Disable() => State = LocationServiceState.Disabled;
        public void PauseUpdates() { }
        public void ResumeUpdates() { }
        public void OpenSystemSettings() { }
        public void Emit(LocationMeasurement measurement) => _fixes.OnNext(measurement);
        public void Dispose() => _fixes.Dispose();
    }
}
