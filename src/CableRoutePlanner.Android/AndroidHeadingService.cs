using Android.App;
using Android.Hardware;
using Android.Views;
using CableRoutePlanner.Application;
using System.Reactive.Subjects;
using HardwareAxis = global::Android.Hardware.Axis;

namespace CableRoutePlanner.Android;

internal sealed class AndroidHeadingService : Java.Lang.Object, IHeadingService, ISensorEventListener
{
    private readonly Activity _activity;
    private readonly SensorManager _sensorManager;
    private readonly Sensor? _rotationSensor;
    private readonly Subject<HeadingMeasurement> _measurements = new();
    private readonly HeadingSmoother _smoother = new(0.25);
    private bool _reliable;
    private bool _registered;
    private bool _disposed;

    public AndroidHeadingService(Activity activity)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _sensorManager = (SensorManager?)activity.GetSystemService(global::Android.Content.Context.SensorService)
            ?? throw new InvalidOperationException("Android SensorManager недоступен.");
        _rotationSensor = _sensorManager.GetDefaultSensor(SensorType.RotationVector);
    }

    public bool IsSupported => _rotationSensor is not null;

    public IObservable<HeadingMeasurement> Measurements => _measurements;

    public void PauseUpdates()
    {
        if (_registered)
        {
            _sensorManager.UnregisterListener(this);
            _registered = false;
        }
    }

    public void ResumeUpdates()
    {
        if (!_disposed && !_registered && _rotationSensor is not null)
        {
            _registered = _sensorManager.RegisterListener(this, _rotationSensor, SensorDelay.Ui);
        }
    }

    public void OnAccuracyChanged(Sensor? sensor, SensorStatus accuracy) =>
        _reliable = accuracy is not SensorStatus.Unreliable and not SensorStatus.NoContact;

    public void OnSensorChanged(SensorEvent? sensorEvent)
    {
        if (sensorEvent?.Values is null || sensorEvent.Values.Count < 3)
        {
            return;
        }

        float[] rotationVector = sensorEvent.Values.ToArray();
        float[] rotationMatrix = new float[9];
        SensorManager.GetRotationMatrixFromVector(rotationMatrix, rotationVector);
        (HardwareAxis axisX, HardwareAxis axisY) = ScreenAxes();
        float[] remapped = new float[9];
        SensorManager.RemapCoordinateSystem(rotationMatrix, axisX, axisY, remapped);
        float[] orientation = new float[3];
        SensorManager.GetOrientation(remapped, orientation);
        double rawHeading = HeadingSmoother.Normalize(orientation[0] * 180 / Math.PI);
        double? heading = _smoother.Update(rawHeading, _reliable);
        _measurements.OnNext(new HeadingMeasurement(
            heading ?? rawHeading,
            heading is not null && _reliable,
            DateTimeOffset.UtcNow));
    }

    void IDisposable.Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        PauseUpdates();
        _measurements.OnCompleted();
        _measurements.Dispose();
    }

    private (HardwareAxis X, HardwareAxis Y) ScreenAxes() => _activity.WindowManager?.DefaultDisplay?.Rotation switch
    {
        SurfaceOrientation.Rotation90 => (HardwareAxis.Y, HardwareAxis.MinusX),
        SurfaceOrientation.Rotation180 => (HardwareAxis.MinusX, HardwareAxis.MinusY),
        SurfaceOrientation.Rotation270 => (HardwareAxis.MinusY, HardwareAxis.X),
        _ => (HardwareAxis.X, HardwareAxis.Y)
    };
}
