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
    private readonly ILocationController _locationController;
    private readonly ITrackingSessionStore _sessionStore;
    private readonly BackgroundSessionSaver _sessionSaver;
    private readonly Func<DateTimeOffset> _clock;
    private readonly CompositeDisposable _lifetime = new();
    private readonly CancellationTokenSource _restoreCancellation = new();
    private RouteProgressTracker? _tracker;
    private RouteAlternative? _route;
    private Guid _sessionId;
    private DateTimeOffset _startedUtc;
    private bool _disposed;
    private bool? _isLocationInsideCoverage;

    public TrackingViewModel(
        ILocationController locationController,
        ITrackingSessionStore sessionStore,
        Func<DateTimeOffset>? clock = null)
    {
        _locationController = locationController ?? throw new ArgumentNullException(nameof(locationController));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _sessionSaver = new BackgroundSessionSaver(_sessionStore, HandlePersistenceFailure);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        LocationState = _locationController.State;
        IsLocationEnabled = _locationController.IsEnabled;

        _locationController.Measurements.Subscribe(HandleLocation).DisposeWith(_lifetime);
        _locationController.States.Subscribe(HandleLocationState).DisposeWith(_lifetime);
        _locationController.Headings.Subscribe(HandleHeading).DisposeWith(_lifetime);

        Start = ReactiveCommand.Create(StartTraversal, this.WhenAnyValue(vm => vm.CanStartTraversal));
        Pause = ReactiveCommand.Create(PauseTraversal, this.WhenAnyValue(vm => vm.CanPauseTraversal));
        Resume = ReactiveCommand.Create(ResumeTraversal, this.WhenAnyValue(vm => vm.CanResumeTraversal));
        Finish = ReactiveCommand.Create(FinishTraversal, this.WhenAnyValue(vm => vm.CanFinishTraversal));
        ToggleFollowCommand = ReactiveCommand.Create(ToggleFollow);

        if (_locationController.IsEnabled)
        {
            _ = RestoreLocationAsync(_restoreCancellation.Token);
        }
    }

    public bool IsLocationSupported => _locationController.IsSupported;

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
        await _locationController.SetEnabledAsync(enabled, cancellationToken);
        IsLocationEnabled = _locationController.IsEnabled;
        HandleLocationState(_locationController.State);
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

    public void OpenSystemLocationSettings() => _locationController.OpenSystemSettings();

    public void SetCoverage(bool? isInsideCoverage)
    {
        if (_isLocationInsideCoverage == isInsideCoverage)
        {
            return;
        }

        _isLocationInsideCoverage = isInsideCoverage;
        this.RaisePropertyChanged(nameof(CoverageStatus));
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

    private void HandleHeading(HeadingMeasurement heading)
    {
        if (!_disposed && _locationController.IsEnabled)
        {
            CurrentHeading = heading;
        }
    }

    private void HandleLocationState(LocationServiceState state)
    {
        LocationState = state;
        IsLocationEnabled = _locationController.IsEnabled;
        if (state.Permission is LocationPermissionState.Denied
            or LocationPermissionState.PermanentlyDenied)
        {
            IsLocationEnabled = false;
            ClearLocationDisplay();
        }
        else if (!_locationController.IsEnabled
            || !state.IsEnabled && state.SystemLocation == SystemLocationState.Disabled)
        {
            ClearLocationDisplay();
        }
        else if (state.FixState is LocationFixState.Stale or LocationFixState.Searching
            || state.IsSearching && state.FixState == LocationFixState.None)
        {
            ClearLocationDisplay();
        }

        this.RaisePropertyChanged(nameof(IsLocationSupported));
        this.RaisePropertyChanged(nameof(CompactProgressText));
        this.RaisePropertyChanged(nameof(TrackingStatusText));
    }

    private async Task RestoreLocationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _locationController.RestoreAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception)
        {
            ReportGpsDiagnostic("Не удалось восстановить геолокацию: " + exception.Message);
        }
    }

    private void ReportGpsDiagnostic(string message)
    {
        RxApp.MainThreadScheduler.Schedule(Unit.Default, (_, _) =>
        {
            if (_disposed)
            {
                return Disposable.Empty;
            }

            GpsDiagnosticText = message;
            return Disposable.Empty;
        });
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
        _restoreCancellation.Cancel();
        if (_tracker is not null && IsTraversalActive)
        {
            Progress = _tracker.Pause();
            SaveSession();
        }

        _lifetime.Dispose();
        _restoreCancellation.Dispose();
        _sessionSaver.Dispose();
        Start.Dispose();
        Pause.Dispose();
        Resume.Dispose();
        Finish.Dispose();
        ToggleFollowCommand.Dispose();
    }
}
