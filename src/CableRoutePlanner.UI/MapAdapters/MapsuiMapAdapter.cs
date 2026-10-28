using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Interactivity;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.ViewModels;
using System.Globalization;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling.Layers;
using Mapsui.UI.Avalonia;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace CableRoutePlanner.UI.MapAdapters;

public sealed class MapsuiMapAdapter : IMapAdapter
{
    private const double MapReloadDistanceViewportFraction = 0.25;

    private const double MapReloadResolutionRatio = 1.25;
    private readonly MapControl _control;

    private readonly MemoryLayer _base;

    private readonly RasterizingTileLayer? _tiledBase;

    private readonly MemoryLayer _routes = new("Маршруты")
    {
        Style = null
    };

    private readonly MemoryLayer _points = new("Точки")
    {
        Style = null
    };

    private readonly MemoryLayer _routeVertices = new("Вершины маршрута")
    {
        Style = null
    };

    private IReadOnlyList<WaypointRow> _waypoints = [];

    private IReadOnlyList<RouteVertexRow> _vertices = [];

    private CancellationTokenSource _featurePreparation = new();

    private bool _fitQueued;

    private bool _cameraWasUsed;

    private bool _disposed;

    private bool _initialized;

    private bool _routeExtentShown;

    private readonly MRect _extent;

    private readonly MapsuiPointerInteraction _pointerInteraction;

    private Viewport? _lastMapDataViewport;

    public event Action<GeoBounds, double>? ViewportChanged;

    public event Action<GeoCoordinate>? PointAdded;

    public event Action<Guid, GeoCoordinate>? PointMoved;

    public event Action<Guid>? WaypointSelected;

    public event Action<double>? NorthIndicatorRotationChanged;

    public event Action<int>? RouteVertexSelected;

    public MapsuiMapAdapter(MapControl control, GeoBounds bounds)
        : this(control, bounds, null)
    {
    }

    public MapsuiMapAdapter(MapControl control, GeoBounds bounds, IGeodataRepository? geodata)
    {
        _control = control;
        _base = new MemoryLayer("Офлайн OSM")
        {
            Style = null
        };
        control.Map = new Mapsui.Map
        {
            CRS = "EPSG:3857",
            BackColor = new Color(244, 242, 233)
        };
        if (geodata is null)
        {
            control.Map.Layers.Add(_base);
        }
        else
        {
            var source = new Layer("Офлайн OSM")
            {
                DataSource = new GeodataFeatureProvider(geodata),
                Style = null
            };
            _tiledBase = new RasterizingTileLayer(
                source,
                minTiles: 96,
                maxTiles: 160,
                minExtraTiles: 8,
                maxExtraTiles: 32);
            control.Map.Layers.Add(_tiledBase);
        }
        control.Map.Layers.Add(_routes);
        control.Map.Layers.Add(_routeVertices);
        control.Map.Layers.Add(_points);
        (double x, double y) = SphericalMercator.FromLonLat(
            (bounds.West + bounds.East) / 2,
            (bounds.South + bounds.North) / 2);
        Coordinate bottomLeft = Project(new GeoCoordinate(bounds.South, bounds.West));
        Coordinate topRight = Project(new GeoCoordinate(bounds.North, bounds.East));
        _extent = new MRect(bottomLeft.X, bottomLeft.Y, topRight.X, topRight.Y);
        control.Map.Navigator.Limiter = new OfflineViewportLimiter(_extent);
        control.Map.Navigator.RotationLock = false;
        _pointerInteraction = new MapsuiPointerInteraction(
            control,
            () => _waypoints,
            () => _vertices,
            ToScreen,
            ToGeo,
            coordinate => PointAdded?.Invoke(coordinate),
            (id, coordinate) => PointMoved?.Invoke(id, coordinate),
            id => WaypointSelected?.Invoke(id),
            RenderPoints,
            number => RouteVertexSelected?.Invoke(number),
            () => _cameraWasUsed = true,
            SetRotation);
        control.Map.Navigator.CenterOnAndZoomTo(new MPoint(x, y), 7);
        control.AddHandler(
            Avalonia.Input.InputElement.PointerPressedEvent,
            _pointerInteraction.Pressed,
            RoutingStrategies.Tunnel,
            true);
        control.AddHandler(Avalonia.Input.InputElement.PointerMovedEvent, _pointerInteraction.Moved, RoutingStrategies.Tunnel, true);
        control.AddHandler(
            Avalonia.Input.InputElement.PointerReleasedEvent,
            _pointerInteraction.Released,
            RoutingStrategies.Tunnel,
            true);
        control.PointerCaptureLost += _pointerInteraction.CaptureLost;
        control.SizeChanged += OnMapSizeChanged;
        control.AddHandler(InputElement.PointerWheelChangedEvent, WheelChanged, RoutingStrategies.Tunnel, true);
        control.Map.Navigator.ViewportChanged += OnViewportChanged;
    }

    public void InitializeViewport()
    {
        // Fitting is queued after layout: Android can report a temporary narrow viewport
        // while the responsive panel is being arranged. Fit again until the user moves it.
        _initialized = true;
        QueueInitialFit();
    }

    private void OnMapSizeChanged(object? sender, SizeChangedEventArgs e) => QueueInitialFit();

    private void QueueInitialFit()
    {
        if (!_initialized || _cameraWasUsed || _fitQueued || _disposed)
        {
            return;
        }

        _fitQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _fitQueued = false;
            if (_cameraWasUsed || _disposed)
            {
                return;
            }

            Viewport viewport = _control.Map.Navigator.Viewport;
            if (viewport.Width > 0 && viewport.Height > 0)
            {
                _control.Map.Navigator.ZoomToBox(_extent);
                OnViewportChanged(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(InitializeViewport)));
            }
        }, DispatcherPriority.Loaded);
    }

    private void WheelChanged(object? sender, PointerWheelEventArgs e) => _cameraWasUsed = true;

    internal static Coordinate Project(GeoCoordinate p)
    {
        (double x, double y) = SphericalMercator.FromLonLat(p.Longitude, p.Latitude);
        return new(x, y);
    }

    public async Task SetFeaturesAsync(IReadOnlyList<MapFeature> features)
    {
        if (_disposed || _tiledBase is not null)
        {
            return;
        }

        CancellationTokenSource previous = _featurePreparation;
        CancellationTokenSource current = new();
        _featurePreparation = current;
        previous.Cancel();
        previous.Dispose();
        CancellationToken cancellationToken = current.Token;
        MapFeature[] snapshot = features.ToArray();
        try
        {
            IFeature[] prepared = await Task.Run(
                () => PrepareFeatures(snapshot, cancellationToken), cancellationToken);
            void Publish()
            {
                if (_disposed || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _base.Features = prepared;
                _base.DataHasChanged();
                _control.RefreshGraphics();
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                Publish();
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(Publish);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer viewport or disposal replaced this preparation.
        }
    }

    internal static IFeature[] PrepareFeatures(
        IReadOnlyList<MapFeature> features, CancellationToken cancellationToken)
    {
        var reader = new WKBReader();
        var transform = new MercatorTransformer();
        return features.OrderBy(f => MapsuiStyleFactory.DrawingOrder(f.Kind)).Select(f =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var feature = new GeometryFeature(transform.Transform(reader.Read(f.Wkb)));
            if (f.Kind == TerrainKind.Settlement)
            {
                feature.Styles.Add(
                    new LabelStyle
                    {
                        Text = f.Name,
                        ForeColor = new Color(57, 70, 61),
                        BackColor = new Brush(new Color(255, 255, 255, 180)),
                        CollisionDetection = true
                    });
            }
            else
            {
                foreach (IStyle style in MapsuiStyleFactory.ForTerrain(f.Kind))
                {
                    feature.Styles.Add(style);
                }
            }

            return (IFeature)feature;
        }).ToArray();
    }

    public void SetPoints(IReadOnlyList<WaypointRow> points)
    {
        _waypoints = points.ToArray();
        RenderPoints(_waypoints);
    }

    public void SetRouteVertices(IReadOnlyList<RouteVertexRow> vertices)
    {
        // Bridge transitions are engineering geometry, not extra map waypoints.
        // User waypoints already have their own interactive markers in the points layer.
        _vertices = vertices.Where(vertex => vertex.Kind is not
            (RouteVertexKind.Transition or RouteVertexKind.UserWaypoint)).ToArray();
        _routeVertices.Features = _vertices.Select(vertex =>
        {
            var feature = new GeometryFeature(new Point(Project(vertex.Coordinate)));
            feature.Styles.Add(new SymbolStyle
            {
                Fill = new Brush(vertex.IsSelected ? new Color(183, 75, 36)
                    : vertex.Kind == RouteVertexKind.UserWaypoint ? new Color(112, 72, 161)
                    : vertex.Kind == RouteVertexKind.Transition ? new Color(198, 127, 31)
                    : new Color(23, 107, 85)),
                Outline = new Pen(vertex.IsSelected ? Color.White : new Color(30, 43, 39), vertex.IsSelected ? 4 : 2),
                SymbolScale = vertex.IsSelected ? 1.15
                    : vertex.Kind == RouteVertexKind.Transition ? 1
                    : vertex.Kind == RouteVertexKind.UserWaypoint ? 0.95
                    : 0.8
            });
            feature.Styles.Add(new LabelStyle
            {
                Text = vertex.Number.ToString(CultureInfo.InvariantCulture),
                ForeColor = Color.White,
                CollisionDetection = false,
                HorizontalAlignment = LabelStyle.HorizontalAlignmentEnum.Center,
                VerticalAlignment = LabelStyle.VerticalAlignmentEnum.Center
            });
            return (IFeature)feature;
        }).ToArray();
        _routeVertices.DataHasChanged();
        _control.RefreshGraphics();
    }

    private void RenderPoints(IReadOnlyList<WaypointRow> points)
    {
        _points.Features = points.Select(p =>
        {
            var feature = new GeometryFeature(new Point(Project(p.Coordinate)));
            feature.Styles.Add(
                new SymbolStyle
                {
                    Fill = new Brush(p.IsSelected ? new Color(26, 111, 210) : new Color(21, 109, 85)),
                    Outline = new Pen(Color.White, 2),
                    SymbolScale = 0.8
                });
            feature.Styles.Add(
                new LabelStyle
                {
                    Text = p.Number.ToString(CultureInfo.InvariantCulture),
                    ForeColor = Color.White,
                    BackColor = null,
                    CollisionDetection = false,
                    HorizontalAlignment = LabelStyle.HorizontalAlignmentEnum.Center,
                    VerticalAlignment = LabelStyle.VerticalAlignmentEnum.Center
                });
            return (IFeature)feature;
        }).ToArray();
        _points.DataHasChanged();
        _control.RefreshGraphics();
    }

    public void SetRoutes(IReadOnlyList<RouteAlternative> routes, Guid? selectedId)
    {
        // The active alternative is the only solid route. Rendering all alternatives as solid
        // overlapping polylines made a selection look unchanged on a small phone screen.
        RouteAlternative? selected = routes.FirstOrDefault(route => route.Id == selectedId);
        if (selected is null)
        {
            _routes.Features = Array.Empty<IFeature>();
            _routeExtentShown = false;
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
        if (selected is not null && !_routeExtentShown)
        {
            // Fit all alternatives once per calculation; keep a stable camera while comparing them.
            Coordinate[] coordinates = routes.SelectMany(route => route.Geometry).Select(Project).ToArray();
            double west = coordinates.Min(point => point.X);
            double east = coordinates.Max(point => point.X);
            double south = coordinates.Min(point => point.Y);
            double north = coordinates.Max(point => point.Y);
            // 15% padding, at least 100 projected metres, keeps short routes away from the edge.
            double padding = Math.Max(100, Math.Max(east - west, north - south) * 0.15);
            _routeExtentShown = true;
            _cameraWasUsed = true;
            _control.Map.Navigator.ZoomToBox(new MRect(west - padding, south - padding, east + padding, north + padding));
        }
        _control.RefreshGraphics();
    }

    public void SetAddMode(bool enabled) => _pointerInteraction.SetAddMode(enabled);

    public void ZoomIn()
    {
        _cameraWasUsed = true;
        _control.Map.Navigator.ZoomIn();
    }

    public void ZoomOut()
    {
        _cameraWasUsed = true;
        _control.Map.Navigator.ZoomOut();
    }

    // RotateTo uses Dispatcher.PushFrame even with a zero duration in Mapsui 4.1.
    // SetViewport applies the complete target viewport, preserving camera position.
    public void ResetNorth()
        => SetRotation(0);

    private void SetRotation(double rotation)
    {
        Viewport current = _control.Map.Navigator.Viewport;
        _control.Map.Navigator.SetViewport(
            new Viewport(current.CenterX, current.CenterY, current.Resolution, rotation, current.Width, current.Height),
            0,
            new Mapsui.Animations.Easing(progress => progress));
    }

    private Avalonia.Point ToScreen(GeoCoordinate coordinate)
    {
        Coordinate world = Project(coordinate);
        MPoint screen = _control.Map.Navigator.Viewport.WorldToScreen(world.X, world.Y);
        return new Avalonia.Point(screen.X, screen.Y);
    }

    private GeoCoordinate ToGeo(Avalonia.Point p)
    {
        MPoint world = _control.Map.Navigator.Viewport.ScreenToWorld(p.X, p.Y);
        (double lon, double lat) = SphericalMercator.ToLonLat(world.X, world.Y);
        return new(lat, lon);
    }

    private void OnViewportChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Viewport viewport = _control.Map.Navigator.Viewport;
        if (!_initialized || viewport.Width <= 0 || viewport.Height <= 0)
        {
            return;
        }

        MPoint screenCenter = viewport.WorldToScreen(viewport.CenterX, viewport.CenterY);
        MPoint screenNorth = viewport.WorldToScreen(
            viewport.CenterX,
            viewport.CenterY + Math.Max(viewport.Resolution * 10, 0.01));
        double northAngle = Math.Atan2(
            screenNorth.Y - screenCenter.Y,
            screenNorth.X - screenCenter.X) * 180 / Math.PI + 90;
        NorthIndicatorRotationChanged?.Invoke(northAngle);

        if (_tiledBase is not null)
        {
            return;
        }

        if (!RequiresMapDataReload(viewport))
        {
            return;
        }

        _lastMapDataViewport = viewport;

        GeoCoordinate[] corners = new[]
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

    private bool RequiresMapDataReload(Viewport viewport)
    {
        if (_lastMapDataViewport is not { } previous)
        {
            return true;
        }

        double resolutionRatio = Math.Max(
            viewport.Resolution / previous.Resolution,
            previous.Resolution / viewport.Resolution);
        if (resolutionRatio >= MapReloadResolutionRatio
            || viewport.Width != previous.Width
            || viewport.Height != previous.Height)
        {
            return true;
        }

        double horizontalThreshold = viewport.Width * viewport.Resolution * MapReloadDistanceViewportFraction;
        double verticalThreshold = viewport.Height * viewport.Resolution * MapReloadDistanceViewportFraction;
        return Math.Abs(viewport.CenterX - previous.CenterX) >= horizontalThreshold
            || Math.Abs(viewport.CenterY - previous.CenterY) >= verticalThreshold;
    }

    public void Dispose()
    {
        _disposed = true;
        _featurePreparation.Cancel();
        _featurePreparation.Dispose();
        _tiledBase?.AbortFetch();
        _tiledBase?.Dispose();
        _control.SizeChanged -= OnMapSizeChanged;
        _control.RemoveHandler(InputElement.PointerWheelChangedEvent, WheelChanged);
        _control.RemoveHandler(Avalonia.Input.InputElement.PointerPressedEvent, _pointerInteraction.Pressed);
        _control.RemoveHandler(Avalonia.Input.InputElement.PointerMovedEvent, _pointerInteraction.Moved);
        _control.RemoveHandler(Avalonia.Input.InputElement.PointerReleasedEvent, _pointerInteraction.Released);
        _control.Map.Navigator.ViewportChanged -= OnViewportChanged;
        _control.PointerCaptureLost -= _pointerInteraction.CaptureLost;
        _control.Map.Navigator.PanLock = false;
        _control.Map.Dispose();
    }

}
