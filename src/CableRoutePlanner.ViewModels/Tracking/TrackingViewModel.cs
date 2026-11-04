using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using ReactiveUI;

namespace CableRoutePlanner.ViewModels;

public sealed class TrackingViewModel : ReactiveObject, IDisposable
{
    private readonly ILocationService _locationService;
    private readonly IHeadingService _headingService;
    private readonly ILocationPreferencesStore _preferencesStore;
    private readonly ITrackingSessionStore _sessionStore;
    private readonly Func<DateTimeOffset> _clock;
    private readonly CompositeDisposable _lifetime = new();
    private RouteProgressTracker? _tracker;
    private RouteAlternative? _route;
    private Guid _sessionId;
    private DateTimeOffset _startedUtc;
    private bool _disposed;
    private bool _isLocationEnabled;
    private bool _isFollowing;
    private LocationServiceState _locationState;
    private LocationMeasurement? _currentLocation;
    private HeadingMeasurement? _currentHeading;
    private RouteProgressState? _progress;
    private bool? _isLocationInsideCoverage;

    public TrackingViewModel(
        ILocationService locationService,
        IHeadingService headingService,
        ILocationPreferencesStore preferencesStore,
        ITrackingSessionStore sessionStore,
        Func<DateTimeOffset>? clock = null)
    {
        _locationService = locationService ?? throw new ArgumentNullException(nameof(locationService));
        _headingService = headingService ?? throw new ArgumentNullException(nameof(headingService));
        _preferencesStore = preferencesStore ?? throw new ArgumentNullException(nameof(preferencesStore));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _locationState = _locationService.State;
        _isLocationEnabled = _locationService.IsSupported && _preferencesStore.Load().IsEnabled;

        _locationService.Measurements.Subscribe(HandleLocation).DisposeWith(_lifetime);
        _locationService.States.Subscribe(HandleLocationState).DisposeWith(_lifetime);
        _headingService.Measurements.Subscribe(heading =>
        {
            CurrentHeading = heading;
        }).DisposeWith(_lifetime);

        Start = ReactiveCommand.Create(StartTraversal);
        Pause = ReactiveCommand.Create(PauseTraversal);
        Resume = ReactiveCommand.Create(ResumeTraversal);
        Finish = ReactiveCommand.Create(FinishTraversal);
        ToggleFollowCommand = ReactiveCommand.Create(ToggleFollow);

        if (_isLocationEnabled)
        {
            _ = RestoreEnabledLocationAsync();
        }
    }

    public bool IsLocationSupported => _locationService.IsSupported;

    public bool IsLocationEnabled
    {
        get => _isLocationEnabled;
        private set => this.RaiseAndSetIfChanged(ref _isLocationEnabled, value);
    }

    public LocationServiceState LocationState
    {
        get => _locationState;
        private set => this.RaiseAndSetIfChanged(ref _locationState, value);
    }

    public LocationMeasurement? CurrentLocation
    {
        get => _currentLocation;
        private set => this.RaiseAndSetIfChanged(ref _currentLocation, value);
    }

    public HeadingMeasurement? CurrentHeading
    {
        get => _currentHeading;
        private set => this.RaiseAndSetIfChanged(ref _currentHeading, value);
    }

    public RouteProgressState? Progress
    {
        get => _progress;
        private set => this.RaiseAndSetIfChanged(ref _progress, value);
    }

    public bool IsFollowing
    {
        get => _isFollowing;
        private set => this.RaiseAndSetIfChanged(ref _isFollowing, value);
    }

    public bool IsTraversalActive => Progress?.Status == ProgressTrackingStatus.Active;

    public bool HasRoute => _route is not null;

    public string ProgressText => Progress is not { } progress
        ? "Обход не начат."
        : $"Пройдено {progress.CoveredDistanceMetres:N0} м из {progress.TotalDistanceMetres:N0} м · "
            + $"осталось {progress.RemainingDistanceMetres:N0} м · {progress.CoveragePercent:N0}%";

    public string CoverageStatus => _isLocationInsideCoverage == false
        ? "Местоположение вне покрытия офлайн-карты. GPS-точка показана без подмены координат."
        : "";

    public ReactiveCommand<Unit, Unit> Start { get; }

    public ReactiveCommand<Unit, Unit> Pause { get; }

    public ReactiveCommand<Unit, Unit> Resume { get; }

    public ReactiveCommand<Unit, Unit> Finish { get; }

    public ReactiveCommand<Unit, Unit> ToggleFollowCommand { get; }

    public async Task SetLocationEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!enabled)
        {
            IsLocationEnabled = false;
            _preferencesStore.Save(new LocationPreferences(false));
            _locationService.Disable();
            _headingService.PauseUpdates();
            return;
        }

        LocationServiceState state = await _locationService.EnableAsync(cancellationToken).ConfigureAwait(false);
        HandleLocationState(state);
        bool permissionWasDenied = state.Permission is LocationPermissionState.Denied
            or LocationPermissionState.PermanentlyDenied;
        IsLocationEnabled = !permissionWasDenied;
        _preferencesStore.Save(new LocationPreferences(!permissionWasDenied));
        if (state.IsEnabled)
        {
            _locationService.ResumeUpdates();
            _headingService.ResumeUpdates();
        }
    }

    public void SetRoute(RouteAlternative? route)
    {
        if (_disposed || ReferenceEquals(_route, route))
        {
            return;
        }

        if (_tracker is not null && IsTraversalActive)
        {
            PauseTraversal();
        }

        _route = route;
        _tracker = null;
        Progress = null;
        this.RaisePropertyChanged(nameof(HasRoute));
        if (route is null)
        {
            return;
        }

        RouteProgressTracker tracker = new();
        RouteProgressState initial = tracker.Begin(route);
        RouteProgressSnapshot? saved = _sessionStore.Load();
        if (saved is not null && IsSnapshotForRoute(saved, initial, route))
        {
            Progress = tracker.Restore(saved, route);
            _sessionId = saved.SessionId;
            _startedUtc = saved.StartedUtc;
        }
        else
        {
            Progress = initial;
            _sessionId = Guid.NewGuid();
            _startedUtc = _clock();
        }

        _tracker = tracker;
        NotifyProgressChanged();
    }

    public void StartTraversal()
    {
        if (_route is null || _tracker is null || Progress?.Status == ProgressTrackingStatus.Completed)
        {
            return;
        }

        Progress = _tracker.Resume();
        SaveSession();
        NotifyProgressChanged();
    }

    public void PauseTraversal()
    {
        if (_tracker is null)
        {
            return;
        }

        Progress = _tracker.Pause();
        SaveSession();
        NotifyProgressChanged();
    }

    public void ResumeTraversal()
    {
        if (_tracker is null || Progress?.Status == ProgressTrackingStatus.Completed)
        {
            return;
        }

        Progress = _tracker.Resume();
        NotifyProgressChanged();
    }

    public void FinishTraversal()
    {
        if (_tracker is null)
        {
            return;
        }

        Progress = _tracker.Finish();
        SaveSession();
        NotifyProgressChanged();
    }

    public void ToggleFollow() => IsFollowing = !IsFollowing;

    public void DisableFollowing() => IsFollowing = false;

    public void OpenSystemLocationSettings() => _locationService.OpenSystemSettings();

    public void SetCoverage(bool? isInsideCoverage)
    {
        if (_isLocationInsideCoverage == isInsideCoverage)
        {
            return;
        }

        _isLocationInsideCoverage = isInsideCoverage;
        this.RaisePropertyChanged(nameof(CoverageStatus));
    }

    private async Task RestoreEnabledLocationAsync()
    {
        try
        {
            LocationServiceState state = await _locationService.RestoreAsync(CancellationToken.None);
            if (_disposed)
            {
                return;
            }

            HandleLocationState(state);
            bool permissionWasDenied = state.Permission is LocationPermissionState.Denied
                or LocationPermissionState.PermanentlyDenied;
            IsLocationEnabled = !permissionWasDenied;
            if (state.IsEnabled)
            {
                _locationService.ResumeUpdates();
                _headingService.ResumeUpdates();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocationState = new LocationServiceState(
                false,
                LocationPermissionState.Denied,
                SystemLocationState.Unknown,
                false,
                $"Не удалось восстановить геолокацию: {ex.Message}");
        }
    }

    private void HandleLocation(LocationMeasurement measurement)
    {
        if (_disposed || !IsLocationEnabled)
        {
            return;
        }

        CurrentLocation = measurement;
        if (_tracker is not null && IsTraversalActive)
        {
            Progress = _tracker.Accept(measurement, _clock());
            SaveSession();
            NotifyProgressChanged();
        }
    }

    private void HandleLocationState(LocationServiceState state)
    {
        LocationState = state;
        if (_isLocationEnabled && (state.Permission is LocationPermissionState.Denied
            or LocationPermissionState.PermanentlyDenied))
        {
            IsLocationEnabled = false;
            _preferencesStore.Save(new LocationPreferences(false));
            _headingService.PauseUpdates();
        }
        this.RaisePropertyChanged(nameof(IsLocationSupported));
    }

    private void SaveSession()
    {
        if (_tracker is not null)
        {
            _sessionStore.Save(_tracker.CreateSnapshot(_sessionId, _startedUtc, _clock()));
        }
    }

    private void NotifyProgressChanged()
    {
        this.RaisePropertyChanged(nameof(IsTraversalActive));
        this.RaisePropertyChanged(nameof(ProgressText));
    }

    private static bool IsSnapshotForRoute(
        RouteProgressSnapshot snapshot,
        RouteProgressState initial,
        RouteAlternative route) =>
        string.Equals(snapshot.RouteFingerprint, initial.RouteFingerprint, StringComparison.Ordinal)
        && snapshot.Geometry.Count == route.Geometry.Count
        && snapshot.Geometry.Zip(route.Geometry).All(pair => pair.First == pair.Second);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_tracker is not null && IsTraversalActive)
        {
            Progress = _tracker.Pause();
            SaveSession();
        }

        _lifetime.Dispose();
        Start.Dispose();
        Pause.Dispose();
        Resume.Dispose();
        Finish.Dispose();
        ToggleFollowCommand.Dispose();
    }
}
