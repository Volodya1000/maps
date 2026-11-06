using System.ComponentModel;

namespace CableRoutePlanner.Application;

public sealed class LocationController : ILocationController
{
    private readonly ILocationService _locationService;
    private readonly IHeadingService _headingService;
    private readonly ILocationPreferencesStore _preferencesStore;
    private readonly LocationStateStream _states;
    private readonly IDisposable _locationStateSubscription;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LocationServiceState _state;
    private bool _isEnabled;
    private bool _restoreFailed;
    private bool _disposed;

    public LocationController(
        ILocationService locationService,
        IHeadingService headingService,
        ILocationPreferencesStore preferencesStore)
    {
        _locationService = locationService ?? throw new ArgumentNullException(nameof(locationService));
        _headingService = headingService ?? throw new ArgumentNullException(nameof(headingService));
        _preferencesStore = preferencesStore ?? throw new ArgumentNullException(nameof(preferencesStore));
        _state = _locationService.State;
        _isEnabled = _locationService.IsSupported && _preferencesStore.Load().IsEnabled;
        _states = new LocationStateStream(_state);
        _locationStateSubscription = _locationService.States.Subscribe(
            new ActionObserver<LocationServiceState>(ApplyState));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsSupported => _locationService.IsSupported;

    public bool IsEnabled => _isEnabled;

    public LocationServiceState State => _state;

    public IObservable<LocationMeasurement> Measurements => _locationService.Measurements;

    public IObservable<LocationServiceState> States => _states;

    public IObservable<HeadingMeasurement> Headings => _headingService.Measurements;

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!enabled)
            {
                SetEnabled(false);
                _locationService.Disable();
                ApplyState(_locationService.State);
                _preferencesStore.Save(new LocationPreferences(false));
                return;
            }

            if (!IsSupported || (_isEnabled && _state.IsEnabled && !_restoreFailed))
            {
                return;
            }

            LocationServiceState state = await _locationService.EnableAsync(cancellationToken);
            bool denied = state.Permission is LocationPermissionState.Denied
                or LocationPermissionState.PermanentlyDenied;
            SetEnabled(!denied);
            _restoreFailed = false;
            ApplyState(state);
            _preferencesStore.Save(new LocationPreferences(_isEnabled));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (!_isEnabled || !IsSupported)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            LocationServiceState state;
            try
            {
                state = await _locationService.RestoreAsync(cancellationToken);
            }
            catch
            {
                _restoreFailed = true;
                throw;
            }

            bool denied = state.Permission is LocationPermissionState.Denied
                or LocationPermissionState.PermanentlyDenied;
            SetEnabled(!denied);
            _restoreFailed = false;
            ApplyState(state);
            _preferencesStore.Save(new LocationPreferences(_isEnabled));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void OpenSystemSettings() => _locationService.OpenSystemSettings();

    private void ApplyState(LocationServiceState state)
    {
        if (_disposed)
        {
            return;
        }

        LocationServiceState previousState = _state;
        _state = state;
        bool denied = state.Permission is LocationPermissionState.Denied
            or LocationPermissionState.PermanentlyDenied;
        if (denied)
        {
            SetEnabled(false);
            _preferencesStore.Save(new LocationPreferences(false));
        }

        _states.OnNext(state);
        if (!Equals(previousState, state))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        }

        if (!_isEnabled || denied || !state.IsEnabled && state.SystemLocation == SystemLocationState.Disabled)
        {
            _headingService.PauseUpdates();
        }
        else if (state.IsEnabled)
        {
            _headingService.ResumeUpdates();
        }
    }

    private void SetEnabled(bool enabled)
    {
        if (_isEnabled == enabled)
        {
            return;
        }

        _isEnabled = enabled;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _locationStateSubscription.Dispose();
        _states.OnCompleted();
        _states.Dispose();
        _gate.Dispose();
    }

    private sealed class ActionObserver<T>(Action<T> action) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => action(value);
    }

    private sealed class LocationStateStream(LocationServiceState initial) : IObservable<LocationServiceState>, IDisposable
    {
        private readonly object _sync = new();
        private readonly List<IObserver<LocationServiceState>> _observers = [];
        private LocationServiceState _current = initial;
        private bool _completed;

        public IDisposable Subscribe(IObserver<LocationServiceState> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            lock (_sync)
            {
                if (_completed)
                {
                    observer.OnCompleted();
                    return EmptyDisposable.Instance;
                }

                _observers.Add(observer);
                observer.OnNext(_current);
                return new ObserverSubscription(this, observer);
            }
        }

        public void OnNext(LocationServiceState state)
        {
            IObserver<LocationServiceState>[] observers;
            lock (_sync)
            {
                if (_completed)
                {
                    return;
                }

                _current = state;
                observers = _observers.ToArray();
            }

            foreach (IObserver<LocationServiceState> observer in observers)
            {
                observer.OnNext(state);
            }
        }

        public void OnCompleted()
        {
            IObserver<LocationServiceState>[] observers;
            lock (_sync)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                observers = _observers.ToArray();
                _observers.Clear();
            }

            foreach (IObserver<LocationServiceState> observer in observers)
            {
                observer.OnCompleted();
            }
        }

        public void Dispose() => OnCompleted();

        private void Remove(IObserver<LocationServiceState> observer)
        {
            lock (_sync)
            {
                _observers.Remove(observer);
            }
        }

        private sealed class ObserverSubscription(
            LocationStateStream owner,
            IObserver<LocationServiceState> observer) : IDisposable
        {
            public void Dispose() => owner.Remove(observer);
        }

        private sealed class EmptyDisposable : IDisposable
        {
            public static EmptyDisposable Instance { get; } = new();
            public void Dispose() { }
        }
    }
}
