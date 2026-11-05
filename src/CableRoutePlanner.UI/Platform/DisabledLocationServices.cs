using System.Reactive.Linq;
using CableRoutePlanner.Application;

namespace CableRoutePlanner.UI;

internal sealed class DisabledLocationService : ILocationService
{
    public bool IsSupported => false;

    public LocationServiceState State => LocationServiceState.Disabled;

    public IObservable<LocationMeasurement> Measurements => Observable.Empty<LocationMeasurement>();

    public IObservable<LocationServiceState> States => Observable.Empty<LocationServiceState>();

    public Task<LocationServiceState> EnableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(LocationServiceState.Disabled);

    public Task<LocationServiceState> RestoreAsync(CancellationToken cancellationToken) =>
        Task.FromResult(LocationServiceState.Disabled);

    public void Disable() { }

    public void PauseUpdates() { }

    public void ResumeUpdates() { }

    public void OpenSystemSettings() { }

    public void Dispose() { }
}

internal sealed class DisabledHeadingService : IHeadingService
{
    public bool IsSupported => false;

    public IObservable<HeadingMeasurement> Measurements => Observable.Empty<HeadingMeasurement>();

    public void PauseUpdates() { }

    public void ResumeUpdates() { }

    public void Dispose() { }
}
