using System.Reactive.Linq;
using CableRoutePlanner.Application;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class LocationControllerTests
{
    [Fact]
    public async Task EnablingAndDisablingLocationIsSerializedAndPersistsOnePreference()
    {
        FakeLocationService location = new();
        FakeHeadingService heading = new();
        MemoryLocationPreferences preferences = new();
        using LocationController controller = new(location, heading, preferences);

        Task first = controller.SetEnabledAsync(true);
        Task second = controller.SetEnabledAsync(true);
        await Task.WhenAll(first, second);

        Assert.Equal(1, location.EnableCalls);
        Assert.True(controller.IsEnabled);
        Assert.True(preferences.Current.IsEnabled);
        Assert.Equal(1, heading.ResumeCalls);

        await controller.SetEnabledAsync(false);

        Assert.False(controller.IsEnabled);
        Assert.False(preferences.Current.IsEnabled);
        Assert.Equal(1, location.DisableCalls);
        Assert.Equal(1, heading.PauseCalls);
    }

    private sealed class FakeLocationService : ILocationService
    {
        public int EnableCalls { get; private set; }
        public int DisableCalls { get; private set; }
        public bool IsSupported => true;
        public LocationServiceState State { get; private set; } = LocationServiceState.Disabled;
        public IObservable<LocationMeasurement> Measurements { get; } = Observable.Empty<LocationMeasurement>();
        public IObservable<LocationServiceState> States { get; } = Observable.Empty<LocationServiceState>();

        public async Task<LocationServiceState> EnableAsync(CancellationToken cancellationToken)
        {
            EnableCalls++;
            await Task.Delay(10, cancellationToken);
            State = LocationServiceState.Enabled;
            return State;
        }

        public Task<LocationServiceState> RestoreAsync(CancellationToken cancellationToken) => Task.FromResult(State);
        public void Disable() { DisableCalls++; State = LocationServiceState.Disabled; }
        public void PauseUpdates() { }
        public void ResumeUpdates() { }
        public void OpenSystemSettings() { }
        public void Dispose() { }
    }

    private sealed class FakeHeadingService : IHeadingService
    {
        public int ResumeCalls { get; private set; }
        public int PauseCalls { get; private set; }
        public bool IsSupported => true;
        public IObservable<HeadingMeasurement> Measurements { get; } = Observable.Empty<HeadingMeasurement>();
        public void PauseUpdates() { PauseCalls++; }
        public void ResumeUpdates() { ResumeCalls++; }
        public void Dispose() { }
    }

    private sealed class MemoryLocationPreferences : ILocationPreferencesStore
    {
        public LocationPreferences Current { get; private set; } = new();
        public LocationPreferences Load() => Current;
        public void Save(LocationPreferences preferences) => Current = preferences;
    }
}
