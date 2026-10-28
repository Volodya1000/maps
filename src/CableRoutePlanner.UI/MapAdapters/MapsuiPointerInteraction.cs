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
    private readonly Func<IReadOnlyList<RouteVertexRow>> _routeVertices;
    private readonly Func<GeoCoordinate, Avalonia.Point> _toScreen;
    private readonly Func<Avalonia.Point, GeoCoordinate> _toGeo;
    private readonly Action<GeoCoordinate> _pointAdded;
    private readonly Action<Guid, GeoCoordinate> _pointMoved;
    private readonly Action<Guid> _waypointSelected;
    private readonly Action<IReadOnlyList<WaypointRow>> _previewChanged;
    private readonly Action<int> _routeVertexSelected;
    private readonly Action _cameraUsed;
    private readonly Action<double> _rotate;
    private readonly HashSet<long> _pointers = [];
    private Avalonia.Point _pressed;
    private Guid? _dragged;
    private bool _multiTouch;
    private bool _addMode;
    private long? _rotatingPointer;
    private Avalonia.Point _rotationCenter;
    private double _rotationStartAngle;
    private double _rotationStart;
    private GeoCoordinate? _lastDragCoordinate;
    private int? _pressedRouteVertex;

    public MapsuiPointerInteraction(
        MapControl control,
        Func<IReadOnlyList<WaypointRow>> waypoints,
        Func<IReadOnlyList<RouteVertexRow>> routeVertices,
        Func<GeoCoordinate, Avalonia.Point> toScreen,
        Func<Avalonia.Point, GeoCoordinate> toGeo,
        Action<GeoCoordinate> pointAdded,
        Action<Guid, GeoCoordinate> pointMoved,
        Action<Guid> waypointSelected,
        Action<IReadOnlyList<WaypointRow>> previewChanged,
        Action<int> routeVertexSelected,
        Action cameraUsed,
        Action<double> rotate)
    {
        _control = control;
        _waypoints = waypoints;
        _routeVertices = routeVertices;
        _toScreen = toScreen;
        _toGeo = toGeo;
        _pointAdded = pointAdded;
        _pointMoved = pointMoved;
        _waypointSelected = waypointSelected;
        _previewChanged = previewChanged;
        _routeVertexSelected = routeVertexSelected;
        _cameraUsed = cameraUsed;
        _rotate = rotate;
    }

    public void SetAddMode(bool enabled) => _addMode = enabled;

    public void Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse
            && e.GetCurrentPoint(_control).Properties.IsRightButtonPressed)
        {
            Mapsui.Viewport viewport = _control.Map.Navigator.Viewport;
            Point position = e.GetPosition(_control);
            _rotationCenter = new(viewport.Width / 2, viewport.Height / 2);
            _rotationStartAngle = PointerAngle(position);
            _rotationStart = viewport.Rotation;
            _rotatingPointer = e.Pointer.Id;
            _pointers.Add(e.Pointer.Id);
            _cameraUsed();
            e.Pointer.Capture(_control);
            e.Handled = true;
            return;
        }

        if (e.Pointer.Type == PointerType.Mouse && !e.GetCurrentPoint(_control).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _cameraUsed();
        _pointers.Add(e.Pointer.Id);
        if (_pointers.Count > 1)
        {
            _multiTouch = true;
            _dragged = null;
            _pressedRouteVertex = null;
            _control.Map.Navigator.PanLock = false;
            return;
        }

        _pressed = e.GetPosition(_control);
        _lastDragCoordinate = null;
        _multiTouch = false;
        _dragged = _waypoints().FirstOrDefault(point => IsMarkerHit(point, _pressed))?.Id;
        if (_dragged is not null)
        {
            _control.Map.Navigator.PanLock = true;
            e.Pointer.Capture(_control);
            e.Handled = true;
            return;
        }

        // Labels are staggered, so two route markers may share a touch radius.
        // Pick the nearest numbered vertex, not the first row in the list.
        _pressedRouteVertex = _routeVertices()
            .Where(vertex => vertex.Kind != RouteVertexKind.UserWaypoint
                && RouteVertexDistanceSquared(vertex, _pressed) <= MarkerHitRadius * MarkerHitRadius)
            .MinBy(vertex => RouteVertexDistanceSquared(vertex, _pressed))?.Number;
        if (_pressedRouteVertex is not null)
        {
            _control.Map.Navigator.PanLock = true;
            e.Pointer.Capture(_control);
            e.Handled = true;
            return;
        }

    }

    public void Moved(object? sender, PointerEventArgs e)
    {
        if (_rotatingPointer == e.Pointer.Id)
        {
            double delta = PointerAngle(e.GetPosition(_control)) - _rotationStartAngle;
            _rotate(_rotationStart + delta * 180 / Math.PI);
            e.Handled = true;
            return;
        }

        if (_pressedRouteVertex is not null)
        {
            e.Handled = true;
            return;
        }

        if (_dragged is not { } dragged || _multiTouch)
        {
            return;
        }

        GeoCoordinate coordinate = _toGeo(e.GetPosition(_control));
        _lastDragCoordinate = coordinate;
        _previewChanged(_waypoints().Select(p => p.Id == dragged ? p with { Coordinate = coordinate } : p).ToArray());
        e.Handled = true;
    }

    public void Released(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pointers.Remove(e.Pointer.Id))
        {
            return;
        }

        Point position = e.GetPosition(_control);
        if (_rotatingPointer == e.Pointer.Id)
        {
            _rotatingPointer = null;
            e.Pointer.Capture(null);
            e.Handled = true;
            _multiTouch = false;
            return;
        }

        if (_pressedRouteVertex is { } routeVertex)
        {
            _pressedRouteVertex = null;
            _control.Map.Navigator.PanLock = false;
            e.Pointer.Capture(null);
            if (IsClick(position))
            {
                _routeVertexSelected(routeVertex);
            }

            e.Handled = true;
            return;
        }

        if (_dragged is { } dragged)
        {
            _dragged = null;
            _control.Map.Navigator.PanLock = false;
            e.Pointer.Capture(null);
            if (IsClick(position))
            {
                _waypointSelected(dragged);
            }
            else
            {
                _pointMoved(dragged, _lastDragCoordinate ?? _toGeo(position));
            }
            e.Handled = true;
        }
        else if (_addMode && !_multiTouch && IsClick(position))
        {
            _pointAdded(_toGeo(position));
        }

        if (_pointers.Count == 0)
        {
            _multiTouch = false;
        }
    }

    public void CaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _dragged = null;
        _pressedRouteVertex = null;
        _rotatingPointer = null;
        _control.Map.Navigator.PanLock = false;
        _pointers.Remove(e.Pointer.Id);
        _previewChanged(_waypoints());
    }

    private double PointerAngle(Avalonia.Point point) =>
        Math.Atan2(point.Y - _rotationCenter.Y, point.X - _rotationCenter.X);

    private bool IsMarkerHit(WaypointRow point, Avalonia.Point position)
    {
        Point screen = _toScreen(point.Coordinate);
        double dx = screen.X - position.X;
        double dy = screen.Y - position.Y;
        return dx * dx + dy * dy <= MarkerHitRadius * MarkerHitRadius;
    }

    private double RouteVertexDistanceSquared(RouteVertexRow vertex, Avalonia.Point position)
    {
        Point screen = _toScreen(vertex.Coordinate);
        double dx = screen.X - position.X;
        double dy = screen.Y - position.Y;
        return dx * dx + dy * dy;
    }

    private bool IsClick(Avalonia.Point position)
    {
        double dx = position.X - _pressed.X;
        double dy = position.Y - _pressed.Y;
        return dx * dx + dy * dy < ClickMovementRadius * ClickMovementRadius;
    }
}
