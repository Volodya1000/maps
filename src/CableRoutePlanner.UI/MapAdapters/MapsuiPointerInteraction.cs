using Avalonia;
using Avalonia.Input;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.ViewModels;
using Mapsui.UI.Avalonia;

namespace CableRoutePlanner.UI.MapAdapters;

internal sealed class MapsuiPointerInteraction
{
    private const double MarkerHitRadius = 22;
    private const double ClickMovementRadius = 5;

    private readonly MapControl _control;
    private readonly Func<IReadOnlyList<WaypointRow>> _waypoints;
    private readonly Func<GeoCoordinate, Avalonia.Point> _toScreen;
    private readonly Func<Avalonia.Point, GeoCoordinate> _toGeo;
    private readonly Action<GeoCoordinate> _pointAdded;
    private readonly Action<Guid, GeoCoordinate> _pointMoved;
    private readonly Action<IReadOnlyList<WaypointRow>> _previewChanged;
    private readonly Action _cameraUsed;
    private readonly HashSet<long> _pointers = [];
    private Avalonia.Point _pressed;
    private Guid? _dragged;
    private bool _multiTouch;
    private bool _addMode;

    public MapsuiPointerInteraction(
        MapControl control,
        Func<IReadOnlyList<WaypointRow>> waypoints,
        Func<GeoCoordinate, Avalonia.Point> toScreen,
        Func<Avalonia.Point, GeoCoordinate> toGeo,
        Action<GeoCoordinate> pointAdded,
        Action<Guid, GeoCoordinate> pointMoved,
        Action<IReadOnlyList<WaypointRow>> previewChanged,
        Action cameraUsed)
    {
        _control = control;
        _waypoints = waypoints;
        _toScreen = toScreen;
        _toGeo = toGeo;
        _pointAdded = pointAdded;
        _pointMoved = pointMoved;
        _previewChanged = previewChanged;
        _cameraUsed = cameraUsed;
    }

    public void SetAddMode(bool enabled) => _addMode = enabled;

    public void Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse && !e.GetCurrentPoint(_control).Properties.IsLeftButtonPressed)
            return;

        _cameraUsed();
        _pointers.Add(e.Pointer.Id);
        if (_pointers.Count > 1)
        {
            _multiTouch = true;
            _dragged = null;
            _control.Map.Navigator.PanLock = false;
            return;
        }

        _pressed = e.GetPosition(_control);
        _multiTouch = false;
        _dragged = _waypoints().FirstOrDefault(point => IsMarkerHit(point, _pressed))?.Id;
        if (_dragged is not null)
        {
            _control.Map.Navigator.PanLock = true;
            e.Pointer.Capture(_control);
            e.Handled = true;
        }
    }

    public void Moved(object? sender, PointerEventArgs e)
    {
        if (_dragged is not { } dragged || _multiTouch)
            return;

        var coordinate = _toGeo(e.GetPosition(_control));
        _previewChanged(_waypoints().Select(p => p.Id == dragged ? p with { Coordinate = coordinate } : p).ToArray());
        e.Handled = true;
    }

    public void Released(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pointers.Remove(e.Pointer.Id))
            return;

        var position = e.GetPosition(_control);
        if (_dragged is { } dragged)
        {
            _dragged = null;
            _control.Map.Navigator.PanLock = false;
            e.Pointer.Capture(null);
            _pointMoved(dragged, _toGeo(position));
            e.Handled = true;
        }
        else if (_addMode && !_multiTouch && IsClick(position))
        {
            _pointAdded(_toGeo(position));
        }

        if (_pointers.Count == 0)
            _multiTouch = false;
    }

    public void CaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _dragged = null;
        _control.Map.Navigator.PanLock = false;
        _pointers.Remove(e.Pointer.Id);
        _previewChanged(_waypoints());
    }

    private bool IsMarkerHit(WaypointRow point, Avalonia.Point position)
    {
        var screen = _toScreen(point.Coordinate);
        var dx = screen.X - position.X;
        var dy = screen.Y - position.Y;
        return dx * dx + dy * dy <= MarkerHitRadius * MarkerHitRadius;
    }

    private bool IsClick(Avalonia.Point position)
    {
        var dx = position.X - _pressed.X;
        var dy = position.Y - _pressed.Y;
        return dx * dx + dy * dy < ClickMovementRadius * ClickMovementRadius;
    }
}
