using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace CableRoutePlanner.ViewModels;

public sealed class TrackingViewModel : ReactiveObject, ITrackingViewModel, IDisposable
{
    private static readonly RouteProgressConfiguration TrackingConfiguration = new();
    private readonly ILocationService _locationService;
    private readonly IHeadingService _headingService;
    private readonly ILocationPreferencesStore _preferencesStore;
    private readonly ITrackingSessionStore _sessionStore;
    private readonly BackgroundSessionSaver _sessionSaver;
    private readonly Func<DateTimeOffset> _clock;
    private readonly CompositeDisposable _lifetime = new();
    private RouteProgressTracker? _tracker;
    private RouteAlternative? _route;
    private Guid _sessionId;
    private DateTimeOffset _startedUtc;
    private bool _disposed;
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
        _sessionSaver = new BackgroundSessionSaver(_sessionStore, HandlePersistenceFailure);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        LocationState = _locationService.State;
        IsLocationEnabled = _locationService.IsSupported && _preferencesStore.Load().IsEnabled;

        _locationService.Measurements.Subscribe(HandleLocation).DisposeWith(_lifetime);
        _locationService.States.Subscribe(HandleLocationState).DisposeWith(_lifetime);
        _headingService.Measurements.Subscribe(heading => CurrentHeading = heading).DisposeWith(_lifetime);

        Start = ReactiveCommand.Create(StartTraversal, this.WhenAnyValue(vm => vm.CanStartTraversal));
        Pause = ReactiveCommand.Create(PauseTraversal, this.WhenAnyValue(vm => vm.CanPauseTraversal));
        Resume = ReactiveCommand.Create(ResumeTraversal, this.WhenAnyValue(vm => vm.CanResumeTraversal));
        Finish = ReactiveCommand.Create(FinishTraversal, this.WhenAnyValue(vm => vm.CanFinishTraversal));
        ToggleFollowCommand = ReactiveCommand.Create(ToggleFollow);

        if (IsLocationEnabled)
        {
            _ = RestoreEnabledLocationAsync();
        }
    }

    public bool IsLocationSupported => _locationService.IsSupported;

    [Reactive] public bool IsLocationEnabled { get; private set; }

    [Reactive] public LocationServiceState LocationState { get; private set; } = LocationServiceState.Disabled;

    [Reactive] public LocationMeasurement? CurrentLocation { get; private set; }

    [Reactive] public HeadingMeasurement? CurrentHeading { get; private set; }

    [Reactive] public RouteProgressState? Progress { get; private set; }

    [Reactive] public bool IsFollowing { get; private set; }

    public bool IsTraversalActive => Progress?.Status == ProgressTrackingStatus.Active;

    public bool CanStartTraversal => Progress?.Status == ProgressTrackingStatus.Ready;

    public bool CanPauseTraversal => Progress?.Status == ProgressTrackingStatus.Active;

    public bool CanResumeTraversal => Progress?.Status == ProgressTrackingStatus.Paused;

    public bool CanFinishTraversal => Progress?.Status is ProgressTrackingStatus.Active or ProgressTrackingStatus.Paused;

    public bool HasRoute => _route is not null;

    public string ProgressText => Progress is not { } progress
        ? "Прокладка кабеля не начата."
        : $"Пройдено {progress.CoveredDistanceMetres:N0} м из {progress.TotalDistanceMetres:N0} м · "
            + $"осталось {progress.RemainingDistanceMetres:N0} м · {progress.CoveragePercent:N0}%";

    public string CompactProgressText => Progress is not { } progress || progress.Status == ProgressTrackingStatus.Ready
        ? "Прокладка: не начата"
        : $"Прокладка: {CompactTrackingState}{progress.CoveragePercent:N0}% · {progress.CoveredDistanceMetres:N0}/{progress.TotalDistanceMetres:N0} м";

    private string CompactTrackingState => Progress?.Status switch
    {
        ProgressTrackingStatus.Paused => "пауза · ",
        ProgressTrackingStatus.Completed => "завершена · ",
        ProgressTrackingStatus.Active when LocationState.FixState == LocationFixState.Stale => "нет свежего GPS · ",
        _ => Progress?.RejectionReason switch
        {
            ProgressRejectionReason.PoorAccuracy => "точность GPS · ",
            ProgressRejectionReason.FutureTimestamp or ProgressRejectionReason.NonIncreasingTimestamp => "время GPS · ",
            ProgressRejectionReason.StaleTimestamp or ProgressRejectionReason.SignalGap => "нет свежего GPS · ",
            ProgressRejectionReason.OffRoute => "вне трассы · ",
            ProgressRejectionReason.InvalidSpeed or ProgressRejectionReason.ImplausibleJump => "скачок GPS · ",
            _ => ""
        }
    };

    [Reactive] public string GpsDiagnosticText { get; private set; } = "GPS-измерения ещё не поступили.";

    private string RejectionDescription => Progress?.RejectionReason switch
    {
        ProgressRejectionReason.PoorAccuracy => $"Точность GPS ±{CurrentLocation?.AccuracyMetres:N0} м; требуется не хуже ±{TrackingConfiguration.MaxAccuracyMetres:N0} м.",
        ProgressRejectionReason.FutureTimestamp => "Время GPS опережает часы приложения; измерение отклонено.",
        ProgressRejectionReason.StaleTimestamp => "GPS-измерение устарело; ожидаются свежие координаты.",
        ProgressRejectionReason.NonIncreasingTimestamp => "GPS-время повторяется или идёт назад; участок не засчитан.",
        ProgressRejectionReason.SignalGap => "Разрыв GPS-сигнала. Учёт продолжится по новым последовательным измерениям.",
        ProgressRejectionReason.OffRoute => $"GPS-точка дальше {TrackingConfiguration.ProjectionToleranceMetres:N0} м от трассы.",
        ProgressRejectionReason.InvalidSpeed => $"Скорость GPS недостоверна; допустимо до {TrackingConfiguration.MaxSpeedMetresPerSecond:N0} м/с.",
        ProgressRejectionReason.ImplausibleJump => "Обнаружен скачок GPS или неоднозначный переход между участками трассы.",
        _ => ""
    };

    public string TrackingStatusText => Progress?.Status switch
    {
        ProgressTrackingStatus.Active when LocationState.FixState == LocationFixState.Stale =>
            "Прокладка начата. GPS-сигнал потерян; ожидаются свежие координаты.",
        ProgressTrackingStatus.Active when Progress.RejectionReason != ProgressRejectionReason.None =>
            "Прокладка начата. " + RejectionDescription,
        ProgressTrackingStatus.Active => Progress.LocationQuality switch
        {
            LocationQuality.Invalid => "Прокладка начата. GPS-измерение неточное или вне трассы; прогресс не засчитан.",
            LocationQuality.Stale => "Прокладка начата. Ожидание свежих GPS-координат.",
            LocationQuality.Unknown => "Прокладка начата. Ожидание GPS-измерений.",
            _ => "Прокладка начата. Учитываются пройденные участки трассы."
        },
        ProgressTrackingStatus.Paused => "Прокладка приостановлена.",
        ProgressTrackingStatus.Completed => "Прокладка завершена.",
        _ => "Прокладка ещё не начата."
    };

    public string CoverageStatus => _isLocationInsideCoverage == false
        ? "Местоположение вне покрытия офлайн-карты. GPS-точка показана без подмены координат."
        : "";

    [Reactive] public string PersistenceStatus { get; private set; } = "";

    public bool HasPersistenceError => !string.IsNullOrWhiteSpace(PersistenceStatus);

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
            _locationService.Disable();
            _headingService.PauseUpdates();
            ClearLocationDisplay();
            _preferencesStore.Save(new LocationPreferences(false));
            return;
        }

        LocationServiceState state = await _locationService.EnableAsync(cancellationToken);
        bool permissionWasDenied = state.Permission is LocationPermissionState.Denied
            or LocationPermissionState.PermanentlyDenied;
        IsLocationEnabled = !permissionWasDenied;
        HandleLocationState(state);
        _preferencesStore.Save(new LocationPreferences(!permissionWasDenied));
        if (permissionWasDenied)
        {
            ClearLocationDisplay();
        }
        if (state.IsEnabled)
        {
            _locationService.ResumeUpdates();
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

        RouteProgressTracker tracker = new(TrackingConfiguration);
        RouteProgressState initial = tracker.Begin(route);
        RouteProgressSnapshot? saved = null;
        try
        {
            saved = _sessionStore.Load();
        }
        catch (Exception ex)
        {
            ReportPersistenceError("Не удалось восстановить сохранённый обход: " + ex.Message);
        }
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
        if (!CanStartTraversal || _tracker is null)
        {
            return;
        }

        Progress = _tracker.Resume();
        SaveSession();
        NotifyProgressChanged();
    }

    public void PauseTraversal()
    {
        if (!CanPauseTraversal || _tracker is null)
        {
            return;
        }

        Progress = _tracker.Pause();
        SaveSession();
        NotifyProgressChanged();
    }

    public void ResumeTraversal()
    {
        if (!CanResumeTraversal || _tracker is null)
        {
            return;
        }

        Progress = _tracker.Resume();
        SaveSession();
        NotifyProgressChanged();
    }

    public void FinishTraversal()
    {
        if (!CanFinishTraversal || _tracker is null)
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

    private void ClearLocationDisplay()
    {
        CurrentLocation = null;
        CurrentHeading = null;
        SetCoverage(null);
    }

    private void HandleLocation(LocationMeasurement measurement)
    {
        if (_disposed || !IsLocationEnabled)
        {
            return;
        }

        CurrentLocation = measurement;
        GpsDiagnosticText = $"GPS: точность ±{measurement.AccuracyMetres:N0} м · возраст {(_clock() - measurement.TimestampUtc).TotalSeconds:N1} с";
        if (_tracker is not null && IsTraversalActive)
        {
            Progress = _tracker.Accept(measurement, _clock());
            QueueSessionSave();
            NotifyProgressChanged();
        }
    }

    private void HandleLocationState(LocationServiceState state)
    {
        LocationState = state;
        if (IsLocationEnabled && (state.Permission is LocationPermissionState.Denied
            or LocationPermissionState.PermanentlyDenied))
        {
            IsLocationEnabled = false;
            _preferencesStore.Save(new LocationPreferences(false));
            _headingService.PauseUpdates();
            ClearLocationDisplay();
        }
        else if (!state.IsEnabled && state.SystemLocation == SystemLocationState.Disabled)
        {
            ClearLocationDisplay();
        }
        else if (state.FixState is LocationFixState.Stale or LocationFixState.Searching
            || state.IsSearching && state.FixState == LocationFixState.None)
        {
            ClearLocationDisplay();
        }

        bool paused = state.IsSearching && state.FixState == LocationFixState.None;
        if (!_disposed && IsLocationEnabled && state.IsEnabled && !paused
            && state.FixState != LocationFixState.None)
        {
            _headingService.ResumeUpdates();
        }
        this.RaisePropertyChanged(nameof(IsLocationSupported));
        this.RaisePropertyChanged(nameof(CompactProgressText));
        this.RaisePropertyChanged(nameof(TrackingStatusText));
    }

    private void SaveSession()
    {
        if (_tracker is not null)
        {
            RouteProgressSnapshot snapshot = _tracker.CreateSnapshot(_sessionId, _startedUtc, _clock());
            _sessionSaver.SaveImmediately(snapshot);
        }
    }

    private void QueueSessionSave()
    {
        if (_tracker is null)
        {
            return;
        }

        RouteProgressSnapshot snapshot = _tracker.CreateSnapshot(_sessionId, _startedUtc, _clock());
        _sessionSaver.QueueSave(snapshot);
    }

    private void HandlePersistenceFailure(Exception exception)
    {
        Console.Error.WriteLine($"Не удалось сохранить сессию обхода: {exception.Message}");
        ReportPersistenceError("Не удалось сохранить прогресс обхода. Проверьте свободное место в памяти устройства.");
    }

    private void ReportPersistenceError(string message)
    {
        RxApp.MainThreadScheduler.Schedule(Unit.Default, (_, _) =>
        {
            if (_disposed)
            {
                return Disposable.Empty;
            }

            PersistenceStatus = message;
            this.RaisePropertyChanged(nameof(HasPersistenceError));
            return Disposable.Empty;
        });
    }

    private void NotifyProgressChanged()
    {
        this.RaisePropertyChanged(nameof(IsTraversalActive));
        this.RaisePropertyChanged(nameof(CanStartTraversal));
        this.RaisePropertyChanged(nameof(CanPauseTraversal));
        this.RaisePropertyChanged(nameof(CanResumeTraversal));
        this.RaisePropertyChanged(nameof(CanFinishTraversal));
        this.RaisePropertyChanged(nameof(ProgressText));
        this.RaisePropertyChanged(nameof(CompactProgressText));
        this.RaisePropertyChanged(nameof(TrackingStatusText));
    }

    private static bool IsSnapshotForRoute(
        RouteProgressSnapshot snapshot,
        RouteProgressState initial,
        RouteAlternative route) =>
        string.Equals(snapshot.RouteFingerprint, initial.RouteFingerprint, StringComparison.Ordinal)
        && RouteProgressGeometry.Matches(snapshot.Geometry, route.Geometry);

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
        _sessionSaver.Dispose();
        Start.Dispose();
        Pause.Dispose();
        Resume.Dispose();
        Finish.Dispose();
        ToggleFollowCommand.Dispose();
    }
}
