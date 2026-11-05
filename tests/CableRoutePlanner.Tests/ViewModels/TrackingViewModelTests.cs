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
        Assert.Equal(1, location.RestoreCalls);
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
    public async Task LocationFixesBeforeStartDoNotCountTowardTraversal()
    {
        FakeLocation location = new();
        using TrackingViewModel viewModel = CreateViewModel(location);
        RouteAlternative route = Route();
        viewModel.SetRoute(route);
        await viewModel.SetLocationEnabledAsync(true);

        location.Emit(Fix(0, Start.AddSeconds(1)));
        location.Emit(Fix(400, Start.AddSeconds(30)));

        Assert.Equal(0, viewModel.Progress!.CoveredDistanceMetres);
        Assert.False(viewModel.IsTraversalActive);
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
        await sessions.WaitForCoveredAsync(400);

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

    [Fact]
    public async Task StaleLocationStateHidesTheLastFixAndRemainsExplicitlyObservable()
    {
        FakeLocation location = new();
        using TrackingViewModel viewModel = CreateViewModel(location);
        await viewModel.SetLocationEnabledAsync(true);
        location.Emit(Fix(100, Start.AddSeconds(1)));

        location.EmitState(new LocationServiceState(
            true,
            LocationPermissionState.Fine,
            SystemLocationState.Enabled,
            true,
            "GPS-сигнал временно отсутствует.",
            LocationFixState.Stale));

        Assert.Null(viewModel.CurrentLocation);
        Assert.Equal(LocationFixState.Stale, viewModel.LocationState.FixState);
    }

    [Fact]
    public async Task ProviderRecoveryResumesHeadingOnlyWhenLocationSettingIsEnabled()
    {
        FakeLocation location = new();
        FakeHeading heading = new();
        using TrackingViewModel viewModel = CreateViewModel(location, heading: heading);

        await viewModel.SetLocationEnabledAsync(true);
        location.EmitState(LocationServiceState.Enabled);
        Assert.Equal(2, heading.ResumeCalls);

        await viewModel.SetLocationEnabledAsync(false);
        location.EmitState(LocationServiceState.Enabled);

        Assert.Equal(2, heading.ResumeCalls);
        Assert.True(heading.PauseCalls > 0);
    }

    [Fact]
    public void TraversalActionsMatchTheCurrentLifecycleState()
    {
        using TrackingViewModel viewModel = CreateViewModel(new FakeLocation());
        viewModel.SetRoute(Route());

        Assert.True(viewModel.CanStartTraversal);
        Assert.False(viewModel.CanPauseTraversal);
        Assert.False(viewModel.CanResumeTraversal);
        Assert.False(viewModel.CanFinishTraversal);

        viewModel.StartTraversal();
        Assert.False(viewModel.CanStartTraversal);
        Assert.True(viewModel.CanPauseTraversal);
        Assert.False(viewModel.CanResumeTraversal);
        Assert.True(viewModel.CanFinishTraversal);

        viewModel.PauseTraversal();
        Assert.False(viewModel.CanStartTraversal);
        Assert.False(viewModel.CanPauseTraversal);
        Assert.True(viewModel.CanResumeTraversal);
        Assert.True(viewModel.CanFinishTraversal);

        viewModel.FinishTraversal();
        Assert.False(viewModel.CanStartTraversal);
        Assert.False(viewModel.CanPauseTraversal);
        Assert.False(viewModel.CanResumeTraversal);
        Assert.False(viewModel.CanFinishTraversal);
    }

    private static TrackingViewModel CreateViewModel(
        FakeLocation location,
        MemoryLocationPreferences? preferences = null,
        MemoryTrackingSessions? sessions = null,
        FakeHeading? heading = null) =>
        new(
            location,
            heading ?? new FakeHeading(),
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
        public int RestoreCalls { get; private set; }
        public int DisableCalls { get; private set; }

        public Task<LocationServiceState> EnableAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnableCalls++;
            State = LocationServiceState.Enabled;
            _states.OnNext(State);
            return Task.FromResult(State);
        }

        public Task<LocationServiceState> RestoreAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoreCalls++;
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
        public void EmitState(LocationServiceState state)
        {
            State = state;
            _states.OnNext(state);
        }
        public void Dispose() { _measurements.Dispose(); _states.Dispose(); }
    }

    private sealed class FakeHeading : IHeadingService
    {
        public bool IsSupported => true;
        public IObservable<HeadingMeasurement> Measurements { get; } = new Subject<HeadingMeasurement>();
        public int PauseCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public void PauseUpdates() => PauseCalls++;
        public void ResumeUpdates() => ResumeCalls++;
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
        public void Save(RouteProgressSnapshot snapshot)
        {
            Value = snapshot;
        }
        public void Clear() => Value = null;
        public async Task WaitForCoveredAsync(double coveredMetres)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(2);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (Value?.CoveredIntervals.Sum(interval => interval.LengthMetres) >= coveredMetres)
                {
                    return;
                }

                await Task.Delay(1);
            }

            throw new TimeoutException("The asynchronous tracking snapshot was not saved in time.");
        }
    }
}
