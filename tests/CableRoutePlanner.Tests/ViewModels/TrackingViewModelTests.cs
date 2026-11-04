using System.Reactive.Subjects;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.ViewModels;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class TrackingViewModelTests : ReactiveTestScope
{
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LocationIsDisabledByDefaultAndDoesNotStartTheService()
    {
        FakeLocation location = new();
        using TrackingViewModel viewModel = CreateViewModel(location);

        Assert.False(viewModel.IsLocationEnabled);
        Assert.Equal(0, location.EnableCalls);
    }

    [Fact]
    public async Task EnablingLocationPersistsTheChoiceAndStartsAfterUserAction()
    {
        FakeLocation location = new();
        MemoryLocationPreferences preferences = new();
        using TrackingViewModel viewModel = CreateViewModel(location, preferences);

        await viewModel.SetLocationEnabledAsync(true);

        Assert.True(viewModel.IsLocationEnabled);
        Assert.True(preferences.Value.IsEnabled);
        Assert.Equal(1, location.EnableCalls);
    }

    [Fact]
    public void PreviouslyEnabledLocationIsRestoredWithoutASecondUserAction()
    {
        FakeLocation location = new();
        MemoryLocationPreferences preferences = new();
        preferences.Save(new LocationPreferences(true));

        using TrackingViewModel viewModel = CreateViewModel(location, preferences);

        Assert.True(viewModel.IsLocationEnabled);
        Assert.Equal(1, location.EnableCalls);
    }

    [Fact]
    public async Task DisablingLocationStopsUpdatesWithoutChangingRoutingOptions()
    {
        FakeLocation location = new();
        using TrackingViewModel viewModel = CreateViewModel(location);
        await viewModel.SetLocationEnabledAsync(true);

        await viewModel.SetLocationEnabledAsync(false);

        Assert.False(viewModel.IsLocationEnabled);
        Assert.Equal(1, location.DisableCalls);
    }

    [Fact]
    public async Task TraversalLifecycleSavesProgressAndRequiresExplicitResumeAfterRestore()
    {
        FakeLocation location = new();
        MemoryTrackingSessions sessions = new();
        using TrackingViewModel viewModel = CreateViewModel(location, sessions: sessions);
        RouteAlternative route = Route();
        viewModel.SetRoute(route);
        await viewModel.SetLocationEnabledAsync(true);
        viewModel.StartTraversal();
        location.Emit(Fix(0, Start.AddSeconds(1)));
        location.Emit(Fix(400, Start.AddSeconds(30)));
        viewModel.PauseTraversal();

        Assert.Equal(400, viewModel.Progress!.CoveredDistanceMetres, 2);
        Assert.NotNull(sessions.Value);

        using TrackingViewModel restored = CreateViewModel(location, sessions: sessions);
        restored.SetRoute(route);

        Assert.False(restored.IsTraversalActive);
        Assert.Equal(400, restored.Progress!.CoveredDistanceMetres, 2);
        restored.ResumeTraversal();
        Assert.True(restored.IsTraversalActive);
    }

    [Fact]
    public async Task ActiveSessionRestoresPausedUntilUserExplicitlyResumes()
    {
        FakeLocation location = new();
        MemoryTrackingSessions sessions = new();
        using TrackingViewModel viewModel = CreateViewModel(location, sessions: sessions);
        RouteAlternative route = Route();
        viewModel.SetRoute(route);
        await viewModel.SetLocationEnabledAsync(true);
        viewModel.StartTraversal();
        location.Emit(Fix(0, Start.AddSeconds(1)));
        location.Emit(Fix(400, Start.AddSeconds(30)));

        using TrackingViewModel restored = CreateViewModel(location, sessions: sessions);
        restored.SetRoute(route);

        Assert.False(restored.IsTraversalActive);
        Assert.Equal(400, restored.Progress!.CoveredDistanceMetres, 2);
    }

    [Fact]
    public async Task DifferentRouteGeometryRejectsRestoredProgress()
    {
        MemoryTrackingSessions sessions = new();
        FakeLocation location = new();
        using TrackingViewModel viewModel = CreateViewModel(location, sessions: sessions);
        RouteAlternative route = Route();
        viewModel.SetRoute(route);
        await viewModel.SetLocationEnabledAsync(true);
        viewModel.StartTraversal();
        location.Emit(Fix(0, Start.AddSeconds(1)));
        location.Emit(Fix(400, Start.AddSeconds(30)));
        viewModel.PauseTraversal();

        using TrackingViewModel changed = CreateViewModel(location, sessions: sessions);
        changed.SetRoute(Route(900));

        Assert.Equal(0, changed.Progress!.CoveredDistanceMetres);
    }

    [Fact]
    public async Task FollowCanBeToggledIndependentlyFromLocationUpdates()
    {
        FakeLocation location = new();
        using TrackingViewModel viewModel = CreateViewModel(location);
        await viewModel.SetLocationEnabledAsync(true);
        location.Emit(Fix(100, Start.AddSeconds(1)));

        Assert.False(viewModel.IsFollowing);
        viewModel.ToggleFollow();
        Assert.True(viewModel.IsFollowing);
        viewModel.ToggleFollow();
        Assert.False(viewModel.IsFollowing);
    }

    private static TrackingViewModel CreateViewModel(
        FakeLocation location,
        MemoryLocationPreferences? preferences = null,
        MemoryTrackingSessions? sessions = null) =>
        new(
            location,
            new FakeHeading(),
            preferences ?? new MemoryLocationPreferences(),
            sessions ?? new MemoryTrackingSessions(),
            () => Start.AddMinutes(1));

    private static RouteAlternative Route(double endMetres = 1_000)
    {
        GeoCoordinate[] geometry = [P(0), P(endMetres)];
        return new(Guid.NewGuid(), "Тест", geometry,
            new RouteMetrics(endMetres, 0, endMetres, 0, 0, 0, endMetres, null));
    }

    private static LocationMeasurement Fix(double metres, DateTimeOffset timestamp) =>
        new(P(metres), 1, timestamp);

    private static GeoCoordinate P(double metres) =>
        new(54, 27 + metres / (111_195.08 * Math.Cos(54 * Math.PI / 180)));

    private sealed class FakeLocation : ILocationService
    {
        private readonly Subject<LocationMeasurement> _measurements = new();
        private readonly Subject<LocationServiceState> _states = new();
        public bool IsSupported => true;
        public LocationServiceState State { get; private set; } = LocationServiceState.Disabled;
        public IObservable<LocationMeasurement> Measurements => _measurements;
        public IObservable<LocationServiceState> States => _states;
        public int EnableCalls { get; private set; }
        public int DisableCalls { get; private set; }

        public Task<LocationServiceState> EnableAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnableCalls++;
            State = LocationServiceState.Enabled;
            _states.OnNext(State);
            return Task.FromResult(State);
        }

        public void Disable()
        {
            DisableCalls++;
            State = LocationServiceState.Disabled;
            _states.OnNext(State);
        }

        public void PauseUpdates() { }
        public void ResumeUpdates() { }
        public void OpenSystemSettings() { }
        public void Emit(LocationMeasurement measurement) => _measurements.OnNext(measurement);
        public void Dispose() { _measurements.Dispose(); _states.Dispose(); }
    }

    private sealed class FakeHeading : IHeadingService
    {
        public bool IsSupported => true;
        public IObservable<HeadingMeasurement> Measurements { get; } = new Subject<HeadingMeasurement>();
        public void PauseUpdates() { }
        public void ResumeUpdates() { }
        public void Dispose() { }
    }

    private sealed class MemoryLocationPreferences : ILocationPreferencesStore
    {
        public LocationPreferences Value { get; private set; } = new();
        public LocationPreferences Load() => Value;
        public void Save(LocationPreferences preferences) => Value = preferences;
    }

    private sealed class MemoryTrackingSessions : ITrackingSessionStore
    {
        public RouteProgressSnapshot? Value { get; private set; }
        public RouteProgressSnapshot? Load() => Value;
        public void Save(RouteProgressSnapshot snapshot) => Value = snapshot;
        public void Clear() => Value = null;
    }
}
