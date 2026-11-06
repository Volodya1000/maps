using System.Reactive.Linq;
using CableRoutePlanner.Application;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class LocationControllerTests
{
    [Fact]
    public async Task IsEnabledNotificationsMatchActualChangesWithoutDuplicates()
    {
        FakeLocationService location = new();
        using LocationController controller = new(location, new FakeHeadingService(), new MemoryLocationPreferences());
        List<string?> changes = [];
        controller.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        await controller.SetEnabledAsync(true);
        await controller.SetEnabledAsync(true);
        await controller.SetEnabledAsync(false);
        await controller.SetEnabledAsync(false);

        Assert.Equal(2, changes.Count(name => name == nameof(controller.IsEnabled)));
        Assert.Contains(nameof(controller.State), changes);
    }

    [Fact]
    public void RevokedPermissionDisablesControllerAndPersistsThatChoice()
    {
        FakeLocationService location = new();
        MemoryLocationPreferences preferences = new() { Current = new LocationPreferences(true) };
        using LocationController controller = new(location, new FakeHeadingService(), preferences);
        List<string?> changes = [];
        controller.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        location.EmitState(new LocationServiceState(
            false,
            LocationPermissionState.Denied,
            SystemLocationState.Enabled,
            false));

        Assert.False(controller.IsEnabled);
        Assert.False(preferences.Current.IsEnabled);
        Assert.Single(changes, name => name == nameof(controller.IsEnabled));
    }

    [Fact]
    public async Task FailedRestoreDoesNotPreventAnExplicitRetry()
    {
        FakeLocationService location = new();
        location.EmitState(LocationServiceState.Enabled);
        location.RestoreException = new InvalidOperationException("temporary failure");
        MemoryLocationPreferences preferences = new() { Current = new LocationPreferences(true) };
        using LocationController controller = new(location, new FakeHeadingService(), preferences);

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RestoreAsync());
        location.RestoreException = null;
        await controller.SetEnabledAsync(true);

        Assert.Equal(1, location.EnableCalls);
        Assert.True(controller.IsEnabled);
    }

    [Fact]
    public async Task RestoreHonorsCancellationWithoutStartingAnotherSession()
    {
        FakeLocationService location = new()
        {
            RestoreCompletion = new TaskCompletionSource<LocationServiceState>()
        };
        MemoryLocationPreferences preferences = new() { Current = new LocationPreferences(true) };
        using LocationController controller = new(location, new FakeHeadingService(), preferences);
        using CancellationTokenSource cancellation = new();

        Task restore = controller.RestoreAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore);
        Assert.Equal(0, location.EnableCalls);
    }
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
        private readonly System.Reactive.Subjects.Subject<LocationServiceState> _states = new();
        public int EnableCalls { get; private set; }
        public int DisableCalls { get; private set; }
        public Exception? RestoreException { get; set; }
        public TaskCompletionSource<LocationServiceState>? RestoreCompletion { get; set; }
        public bool IsSupported => true;
        public LocationServiceState State { get; private set; } = LocationServiceState.Disabled;
        public IObservable<LocationMeasurement> Measurements { get; } = Observable.Empty<LocationMeasurement>();
        public IObservable<LocationServiceState> States => _states;

        public async Task<LocationServiceState> EnableAsync(CancellationToken cancellationToken)
        {
            EnableCalls++;
            await Task.Delay(10, cancellationToken);
            State = LocationServiceState.Enabled;
            return State;
        }

        public Task<LocationServiceState> RestoreAsync(CancellationToken cancellationToken)
        {
            if (RestoreException is not null)
            {
                return Task.FromException<LocationServiceState>(RestoreException);
            }

            if (RestoreCompletion is not null)
            {
                return RestoreCompletion.Task.WaitAsync(cancellationToken);
            }

            return Task.FromResult(State);
        }
        public void Disable() { DisableCalls++; State = LocationServiceState.Disabled; }
        public void PauseUpdates() { }
        public void ResumeUpdates() { }
        public void OpenSystemSettings() { }
        public void EmitState(LocationServiceState state)
        {
            State = state;
            _states.OnNext(state);
        }
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
        public LocationPreferences Current { get; set; } = new();
        public LocationPreferences Load() => Current;
        public void Save(LocationPreferences preferences) => Current = preferences;
    }
}
