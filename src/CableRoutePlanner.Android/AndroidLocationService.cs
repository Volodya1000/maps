using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Locations;
using Android.OS;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using System.Reactive.Subjects;
using AndroidManifest = global::Android.Manifest;

namespace CableRoutePlanner.Android;

internal sealed class AndroidLocationService : Java.Lang.Object, ILocationService
{
    private const int PermissionRequestCode = 4101;
    private const long MinimumUpdateTimeMilliseconds = 1_000;
    private const float MinimumUpdateDistanceMetres = 1;
    private readonly Activity _activity;
    private readonly LocationManager _locationManager;
    private readonly Subject<LocationMeasurement> _measurements = new();
    private readonly Subject<LocationServiceState> _states = new();
    private readonly Listener _listener;
    private Timer? _staleTimer;
    private TaskCompletionSource<LocationServiceState>? _permissionRequest;
    private LocationServiceState _state = LocationServiceState.Disabled;
    private bool _permissionRequested;
    private bool _updatesActive;
    private bool _disposed;
    private DateTimeOffset? _updatesStartedUtc;
    private DateTimeOffset? _lastLocationUtc;

    public AndroidLocationService(Activity activity)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _locationManager = (LocationManager?)activity.GetSystemService(Context.LocationService)
            ?? throw new InvalidOperationException("Android LocationManager недоступен.");
        _listener = new Listener(this);
    }

    public bool IsSupported => true;

    public LocationServiceState State => _state;

    public IObservable<LocationMeasurement> Measurements => _measurements;

    public IObservable<LocationServiceState> States => _states;

    public async Task<LocationServiceState> EnableAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (HasAnyLocationPermission())
        {
            return StartIfProviderEnabled();
        }

        if (_permissionRequested)
        {
            return Publish(new LocationServiceState(
                false,
                PermissionState(),
                SystemLocationState.Unknown,
                false,
                "Разрешение на местоположение отклонено. Разрешите его в настройках Android."));
        }

        _permissionRequested = true;
        _permissionRequest = new TaskCompletionSource<LocationServiceState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration cancellation = cancellationToken.Register(
            () => _permissionRequest.TrySetCanceled(cancellationToken));
        _activity.RequestPermissions(
            [AndroidManifest.Permission.AccessFineLocation, AndroidManifest.Permission.AccessCoarseLocation],
            PermissionRequestCode);
        return await _permissionRequest.Task.ConfigureAwait(false);
    }

    public Task<LocationServiceState> RestoreAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasAnyLocationPermission())
        {
            return Task.FromResult(Publish(new LocationServiceState(
                false,
                PermissionState(),
                SystemLocationState.Unknown,
                false,
                "Доступ к местоположению больше не предоставлен. Включите его в настройках Android.")));
        }

        return Task.FromResult(StartIfProviderEnabled());
    }

    public void OnRequestPermissionsResult()
    {
        if (_disposed)
        {
            return;
        }

        if (HasAnyLocationPermission())
        {
            _permissionRequested = false;
            TaskCompletionSource<LocationServiceState>? request = _permissionRequest;
            _permissionRequest = null;
            request?.TrySetResult(StartIfProviderEnabled());
            return;
        }

        bool canAskAgain = _activity.ShouldShowRequestPermissionRationale(
            AndroidManifest.Permission.AccessFineLocation)
            || _activity.ShouldShowRequestPermissionRationale(AndroidManifest.Permission.AccessCoarseLocation);
        LocationPermissionState permission = canAskAgain
            ? LocationPermissionState.Denied
            : LocationPermissionState.PermanentlyDenied;
        LocationServiceState state = Publish(new LocationServiceState(
            false,
            permission,
            SystemLocationState.Unknown,
            false,
            canAskAgain
                ? "Доступ к местоположению отклонён. Его можно запросить снова из настроек приложения."
                : "Доступ запрещён. Разрешите местоположение в настройках приложения Android."));
        _permissionRequested = canAskAgain;
        TaskCompletionSource<LocationServiceState>? deniedRequest = _permissionRequest;
        _permissionRequest = null;
        deniedRequest?.TrySetResult(state);
    }

    public void Disable()
    {
        if (_disposed)
        {
            return;
        }

        StopUpdates();
        Publish(LocationServiceState.Disabled);
    }

    public void PauseUpdates()
    {
        if (!_disposed)
        {
            StopUpdates();
        }
    }

    public void ResumeUpdates()
    {
        if (_disposed || !_state.IsEnabled)
        {
            return;
        }

        if (!HasAnyLocationPermission())
        {
            Publish(new LocationServiceState(
                false,
                PermissionState(),
                SystemLocationState.Unknown,
                false,
                "Доступ к местоположению отозван. Разрешите его в настройках приложения Android."));
            return;
        }

        if (_state.IsEnabled)
        {
            StartIfProviderEnabled();
        }
    }

    public void OpenSystemSettings()
    {
        Intent intent = new(global::Android.Provider.Settings.ActionLocationSourceSettings);
        _activity.StartActivity(intent);
    }

    void IDisposable.Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopUpdates();
        _permissionRequest?.TrySetCanceled();
        _measurements.OnCompleted();
        _states.OnCompleted();
        _measurements.Dispose();
        _states.Dispose();
        _listener.Dispose();
    }

    private LocationServiceState StartIfProviderEnabled()
    {
        if (!_locationManager.IsProviderEnabled(LocationManager.GpsProvider))
        {
            StopUpdates();
            return Publish(new LocationServiceState(
                false,
                PermissionState(),
                SystemLocationState.Disabled,
                false,
                "Системная геолокация выключена. Включите её в настройках Android."));
        }

        if (!_updatesActive)
        {
            _locationManager.RequestLocationUpdates(
                LocationManager.GpsProvider,
                MinimumUpdateTimeMilliseconds,
                MinimumUpdateDistanceMetres,
                _listener,
                Looper.MainLooper);
            _updatesActive = true;
            _updatesStartedUtc = DateTimeOffset.UtcNow;
            _lastLocationUtc = null;
            _staleTimer = new Timer(CheckStale, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15));
        }

        string? message = PermissionState() == LocationPermissionState.Approximate
            ? "Используется приблизительное местоположение."
            : "Ожидание GPS-сигнала…";
        return Publish(new LocationServiceState(
            true,
            PermissionState(),
            SystemLocationState.Enabled,
            true,
            message));
    }

    private void StopUpdates()
    {
        if (_updatesActive)
        {
            _locationManager.RemoveUpdates(_listener);
            _updatesActive = false;
        }

        _staleTimer?.Dispose();
        _staleTimer = null;
        _updatesStartedUtc = null;
        _lastLocationUtc = null;
    }

    private bool HasAnyLocationPermission() =>
        _activity.CheckSelfPermission(AndroidManifest.Permission.AccessFineLocation) == Permission.Granted
        || _activity.CheckSelfPermission(AndroidManifest.Permission.AccessCoarseLocation) == Permission.Granted;

    private LocationPermissionState PermissionState() =>
        _activity.CheckSelfPermission(AndroidManifest.Permission.AccessFineLocation) == Permission.Granted
            ? LocationPermissionState.Fine
            : _activity.CheckSelfPermission(AndroidManifest.Permission.AccessCoarseLocation) == Permission.Granted
                ? LocationPermissionState.Approximate
                : LocationPermissionState.Denied;

    private LocationServiceState Publish(LocationServiceState state)
    {
        _state = state;
        _states.OnNext(state);
        return state;
    }

    private void PublishLocation(Location location)
    {
        DateTimeOffset timestamp = location.Time > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(location.Time)
            : DateTimeOffset.UtcNow;
        double? speed = location.HasSpeed ? location.Speed : null;
        bool approximate = PermissionState() == LocationPermissionState.Approximate;
        _lastLocationUtc = timestamp;
        _measurements.OnNext(new LocationMeasurement(
            new GeoCoordinate(location.Latitude, location.Longitude),
            location.HasAccuracy ? location.Accuracy : 100,
            timestamp,
            speed,
            approximate));
        Publish(new LocationServiceState(
            true,
            PermissionState(),
            SystemLocationState.Enabled,
            false,
            approximate ? "Используется приблизительное местоположение." : null));
    }

    private void CheckStale(object? state)
    {
        if (_disposed || !_updatesActive)
        {
            return;
        }

        DateTimeOffset reference = _lastLocationUtc ?? _updatesStartedUtc ?? DateTimeOffset.UtcNow;
        if (DateTimeOffset.UtcNow - reference > TimeSpan.FromSeconds(30))
        {
            Publish(new LocationServiceState(
                true,
                PermissionState(),
                SystemLocationState.Enabled,
                true,
                "GPS-сигнал временно отсутствует или координаты устарели."));
        }
    }

    private sealed class Listener(AndroidLocationService owner) : Java.Lang.Object, ILocationListener
    {
        public void OnLocationChanged(Location? location)
        {
            if (location is not null)
            {
                owner.PublishLocation(location);
            }
        }

        public void OnProviderDisabled(string? provider) => owner.Publish(new LocationServiceState(
            false,
            owner.PermissionState(),
            SystemLocationState.Disabled,
            false,
            "Системная геолокация выключена. Включите её в настройках Android."));

        public void OnProviderEnabled(string? provider) => owner.StartIfProviderEnabled();

        public void OnStatusChanged(string? provider, Availability status, Bundle? extras)
        {
            if (status != Availability.Available)
            {
                owner.Publish(new LocationServiceState(
                    true,
                    owner.PermissionState(),
                    SystemLocationState.Enabled,
                    true,
                    "GPS-сигнал временно недоступен."));
            }
        }
    }
}
