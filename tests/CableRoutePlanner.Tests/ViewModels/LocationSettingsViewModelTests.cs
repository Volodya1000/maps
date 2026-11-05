using System.Reactive.Subjects;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.ViewModels;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class LocationSettingsViewModelTests : ReactiveTestScope
{
    [Fact]
    public void LocationIsOffByDefaultAndRoutingOptionsRemainUnchanged()
    {
        MemorySettings settings = new();
        FakeLocation location = new();
        using TrackingViewModel tracking = CreateTracking(location);
        using SettingsViewModel viewModel = new(new NavigationHost(), settings, tracking);

        Assert.False(viewModel.ShowLocation);
        Assert.Equal(TestData.Options, settings.Options);
    }

    [Fact]
    public async Task LocationSettingAppliesImmediatelyAndPersistsSeparately()
    {
        MemorySettings settings = new();
        FakeLocation location = new();
        using TrackingViewModel tracking = CreateTracking(location);
        using SettingsViewModel viewModel = new(new NavigationHost(), settings, tracking);

        await viewModel.SetLocationEnabledAsync(true);

        Assert.True(viewModel.ShowLocation);
        Assert.True(tracking.IsLocationEnabled);
        Assert.Equal(TestData.Options, settings.Options);
    }

    [Fact]
    public async Task ProviderDisabledStateIsExposedWithoutCrashing()
    {
        FakeLocation location = new()
        {
            NextState = new LocationServiceState(
                false,
                LocationPermissionState.Fine,
                SystemLocationState.Disabled,
                false,
                "Включите геолокацию в настройках Android.")
        };
        using TrackingViewModel tracking = CreateTracking(location);
        using SettingsViewModel viewModel = new(new NavigationHost(), new MemorySettings(), tracking);

        await viewModel.SetLocationEnabledAsync(true);

        Assert.True(viewModel.ShowLocation);
        Assert.Contains("Включите", viewModel.LocationStatus);
    }

    private static TrackingViewModel CreateTracking(FakeLocation location) => new(
        location,
        new FakeHeading(),
        new MemoryLocationPreferences(),
        new MemoryTrackingSessions());

    private sealed class FakeLocation : ILocationService
    {
        private readonly Subject<LocationMeasurement> _measurements = new();
        private readonly Subject<LocationServiceState> _states = new();
        public bool IsSupported => true;
        public LocationServiceState State { get; private set; } = LocationServiceState.Disabled;
        public LocationServiceState NextState { get; init; } = LocationServiceState.Enabled;
        public IObservable<LocationMeasurement> Measurements => _measurements;
        public IObservable<LocationServiceState> States => _states;
        public Task<LocationServiceState> EnableAsync(CancellationToken cancellationToken)
        {
            State = NextState;
            _states.OnNext(State);
            return Task.FromResult(State);
        }

        public Task<LocationServiceState> RestoreAsync(CancellationToken cancellationToken) =>
            Task.FromResult(State);
        public void Disable() => State = LocationServiceState.Disabled;
        public void PauseUpdates() { }
        public void ResumeUpdates() { }
        public void OpenSystemSettings() { }
        public void Dispose() { _measurements.Dispose(); _states.Dispose(); }
    }

    private sealed class FakeHeading : IHeadingService
    {
        public bool IsSupported => false;
        public IObservable<HeadingMeasurement> Measurements { get; } = new Subject<HeadingMeasurement>();
        public void PauseUpdates() { }
        public void ResumeUpdates() { }
        public void Dispose() { }
    }

    private sealed class MemoryLocationPreferences : ILocationPreferencesStore
    {
        public LocationPreferences Load() => new();
        public void Save(LocationPreferences preferences) { }
    }

    private sealed class MemoryTrackingSessions : ITrackingSessionStore
    {
        public RouteProgressSnapshot? Load() => null;
        public void Save(RouteProgressSnapshot snapshot) { }
        public void Clear() { }
    }
}
