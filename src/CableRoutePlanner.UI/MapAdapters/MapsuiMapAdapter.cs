using Avalonia.Input;
using Avalonia.Interactivity;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.ViewModels;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.UI.Avalonia;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.IO;

namespace CableRoutePlanner.UI.MapAdapters;

public sealed class MapsuiMapAdapter : IMapAdapter
{
    private readonly MapControl _control;

    private readonly MemoryLayer _base = new("Офлайн OSM")
    {
        Style = null
    };

    private readonly MemoryLayer _routes = new("Маршруты")
    {
        Style = null
    };

    private readonly MemoryLayer _points = new("Точки")
    {
        Style = null
    };

    private IReadOnlyList<WaypointRow> _waypoints = [];

    private readonly HashSet<long> _pointers = [];

    private Guid? _dragged;

    private Avalonia.Point _pressed;

    private bool _multiTouch;

    private bool _addMode;

    private bool _fitted;

    private readonly MRect _extent;

    public event Action<GeoBounds, double>? ViewportChanged;

    public event Action<GeoCoordinate>? PointAdded;

    public event Action<Guid, GeoCoordinate>? PointMoved;

    public MapsuiMapAdapter(MapControl control, GeoBounds bounds)
    {
        _control = control;
        control.Map = new Mapsui.Map
        {
            CRS = "EPSG:3857",
            BackColor = new Color(244, 242, 233)
        };
        control.Map.Layers.Add(_base);
        control.Map.Layers.Add(_routes);
        control.Map.Layers.Add(_points);
        var (x, y) = SphericalMercator.FromLonLat(
            (bounds.West + bounds.East) / 2,
            (bounds.South + bounds.North) / 2);
        var bottomLeft = Project(new GeoCoordinate(bounds.South, bounds.West));
        var topRight = Project(new GeoCoordinate(bounds.North, bounds.East));
        _extent = new MRect(bottomLeft.X, bottomLeft.Y, topRight.X, topRight.Y);
        control.Map.Navigator.CenterOnAndZoomTo(new MPoint(x, y), 7);
        control.AddHandler(
            Avalonia.Input.InputElement.PointerPressedEvent,
            Pressed,
            RoutingStrategies.Tunnel,
            true);
        control.AddHandler(Avalonia.Input.InputElement.PointerMovedEvent, Moved, RoutingStrategies.Tunnel, true);
        control.AddHandler(
            Avalonia.Input.InputElement.PointerReleasedEvent,
            Released,
            RoutingStrategies.Tunnel,
            true);
        control.PointerCaptureLost += CaptureLost;
        control.Map.Navigator.ViewportChanged += OnViewportChanged;
    }

    private static Coordinate Project(GeoCoordinate p)
    {
        var (x, y) = SphericalMercator.FromLonLat(p.Longitude, p.Latitude);
        return new(x, y);
    }

    public void SetFeatures(IReadOnlyList<MapFeature> features)
    {
        var reader = new WKBReader();
        var transform = new MercatorTransformer();
        _base.Features = features.OrderBy(
            f => f.Kind is TerrainKind.Forest or TerrainKind.Field ? 0 : 1).Select(f =>
        {
            var feature = new GeometryFeature(transform.Transform(reader.Read(f.Wkb)));
            if (f.Kind == TerrainKind.Settlement)
            {
                feature.Styles.Add(
                    new LabelStyle { Text = f.Name, ForeColor = new Color(57, 70, 61), BackColor = new Brush(new Color(255, 255, 255, 180)), CollisionDetection = true });
            }
            else
            {
                feature.Styles.Add(Style(f.Kind));
            }

            return (IFeature)feature;
        }).ToArray();
        _base.DataHasChanged();
        _control.RefreshGraphics();
    }

    private static VectorStyle Style(TerrainKind kind)
    {
        var color = kind switch
        {
            TerrainKind.Forest => new Color(188, 214, 176),
            TerrainKind.Field => new Color(231, 224, 179),
            TerrainKind.Lake or TerrainKind.River => new Color(151, 196, 218),
            TerrainKind.Marsh => new Color(141, 179, 168),
            TerrainKind.Building => new Color(201, 197, 188),
            TerrainKind.BridgeCandidate => new Color(168, 143, 113),
            TerrainKind.ApprovedCrossing => new Color(230, 171, 53),
            _ => Color.White
        };
        return new VectorStyle
        {
            Fill = new Brush(color),
            Outline = new Pen(new Color(182, 187, 174), 0.5),
            Line = new Pen(
                kind == TerrainKind.Road ? new Color(195, 182, 155) : color,
                kind == TerrainKind.Road ? 2 : 3)
        };
    }

    public void SetPoints(IReadOnlyList<WaypointRow> points)
    {
        _waypoints = points.ToArray();
        RenderPoints(points);
    }

    private void RenderPoints(IReadOnlyList<WaypointRow> points)
    {
        _points.Features = points.Select(p =>
        {
            var feature = new GeometryFeature(new Point(Project(p.Coordinate)));
            feature.Styles.Add(
                new SymbolStyle { Fill = new Brush(new Color(21, 109, 85)), Outline = new Pen(Color.White, 2), SymbolScale = 0.8 });
            feature.Styles.Add(
                new LabelStyle { Text = p.Number.ToString(), ForeColor = Color.White, BackColor = null, HorizontalAlignment = LabelStyle.HorizontalAlignmentEnum.Center, VerticalAlignment = LabelStyle.VerticalAlignmentEnum.Center });
            return (IFeature)feature;
        }).ToArray();
        _points.DataHasChanged();
        _control.RefreshGraphics();
    }

    public void SetRoutes(IReadOnlyList<RouteAlternative> routes, Guid? selectedId)
    {
        // The active alternative is the only solid route. Rendering all alternatives as solid
        // overlapping polylines made a selection look unchanged on a small phone screen.
        var selected = routes.FirstOrDefault(route => route.Id == selectedId);
        if (selected is null)
        {
            _routes.Features = Array.Empty<IFeature>();
        }
        else
        {
            var feature = new GeometryFeature(new LineString(selected.Geometry.Select(Project).ToArray()));
            feature["route_id"] = selected.Id.ToString();
            feature.Styles.Add(new VectorStyle { Line = new Pen(new Color(26, 111, 210), 6) });
            _routes.Features = new[]
            {
                feature
            };
        }

        _routes.DataHasChanged();
        _control.RefreshGraphics();
    }

    public void SetAddMode(bool enabled) => _addMode = enabled;

    public void ZoomIn() => _control.Map.Navigator.ZoomIn();

    public void ZoomOut() => _control.Map.Navigator.ZoomOut();

    private GeoCoordinate ToGeo(Avalonia.Point p)
    {
        var world = _control.Map.Navigator.Viewport.ScreenToWorld(p.X, p.Y);
        var (lon, lat) = SphericalMercator.ToLonLat(world.X, world.Y);
        return new(lat, lon);
    }

    private void Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse && !e.GetCurrentPoint(_control).Properties.IsLeftButtonPressed)
        {
            return;
        }

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
        foreach (var point in _waypoints)
        {
            var c = Project(point.Coordinate);
            var screen = _control.Map.Navigator.Viewport.WorldToScreen(c.X, c.Y);
            if (Math.Pow(screen.X - _pressed.X, 2) + Math.Pow(screen.Y - _pressed.Y, 2) > 22 * 22)
            {
                continue;
            }

            _dragged = point.Id;
            _control.Map.Navigator.PanLock = true;
            e.Pointer.Capture(_control);
            e.Handled = true;
            break;
        }
    }

    private void Moved(object? sender, PointerEventArgs e)
    {
        if (_dragged is null || _multiTouch)
        {
            return;
        }

        var coordinate = ToGeo(e.GetPosition(_control));
        // Preview belongs to the adapter; the committed coordinate is always stored by the ViewModel.
        var preview = _waypoints.Select(p => p.Id == _dragged ? p with { Coordinate = coordinate } : p).ToArray();
        RenderPoints(preview);
        e.Handled = true;
    }

    private void Released(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pointers.Remove(e.Pointer.Id))
        {
            return;
        }

        var p = e.GetPosition(_control);
        if (_dragged is { } id)
        {
            _dragged = null;
            _control.Map.Navigator.PanLock = false;
            e.Pointer.Capture(null);
            PointMoved?.Invoke(id, ToGeo(p));
            e.Handled = true;
        }
        else if (_addMode && !_multiTouch && Math.Pow(p.X - _pressed.X, 2) + Math.Pow(p.Y - _pressed.Y, 2) < 25)
        {
            PointAdded?.Invoke(ToGeo(p));
        }

        if (_pointers.Count == 0)
        {
            _multiTouch = false;
        }
    }

    private void CaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _dragged = null;
        _control.Map.Navigator.PanLock = false;
        _pointers.Remove(e.Pointer.Id);
        RenderPoints(_waypoints);
    }

    private void OnViewportChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        var viewport = _control.Map.Navigator.Viewport;
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            return;
        }

        if (!_fitted)
        {
            _fitted = true;
            _control.Map.Navigator.ZoomToBox(_extent);
            return;
        }

        var corners = new[]
        {
            ToGeo(new(0, 0)),
            ToGeo(new(viewport.Width, 0)),
            ToGeo(new(0, viewport.Height)),
            ToGeo(new(viewport.Width, viewport.Height))
        };
        ViewportChanged?.Invoke(
            new(
                corners.Min(p => p.Longitude),
                corners.Min(p => p.Latitude),
                corners.Max(p => p.Longitude),
                corners.Max(p => p.Latitude)),
            viewport.Resolution);
    }

    public void Dispose()
    {
        _control.RemoveHandler(Avalonia.Input.InputElement.PointerPressedEvent, Pressed);
        _control.RemoveHandler(Avalonia.Input.InputElement.PointerMovedEvent, Moved);
        _control.RemoveHandler(Avalonia.Input.InputElement.PointerReleasedEvent, Released);
        _control.Map.Navigator.ViewportChanged -= OnViewportChanged;
        _control.PointerCaptureLost -= CaptureLost;
        _control.Map.Navigator.PanLock = false;
        _control.Map.Dispose();
    }

    private sealed class MercatorTransformer : GeometryTransformer
    {
        protected override CoordinateSequence TransformCoordinates(CoordinateSequence coords, Geometry parent) => Factory.CoordinateSequenceFactory.Create(
            coords.ToCoordinateArray().Select(c => Project(new(c.Y, c.X))).ToArray());
    }
}
