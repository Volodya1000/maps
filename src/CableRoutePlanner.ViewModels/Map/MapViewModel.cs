using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace CableRoutePlanner.ViewModels;

public sealed record WaypointRow(Guid Id, int Number, GeoCoordinate Coordinate, bool IsSelected = false)
{
    public string Display => Coordinate.ToString();
}

public enum RouteVertexKind
{
    Endpoint,
    UserWaypoint,
    Transition,
    Turn
}

public sealed record RouteVertexRow(int Number, GeoCoordinate Coordinate, RouteVertexKind Kind = RouteVertexKind.Turn, bool IsSelected = false)
{
    public string Display => Coordinate.ToString();
}

public sealed record WaypointReorder(Guid PointId, int TargetIndex);

public sealed record RouteChoice(Guid Id, string Name, string LengthText, bool IsSelected);

public sealed class MapViewModel : ReactiveObject, IRoutableViewModel, IDisposable
{
    private readonly IPlanningSession _session;

    private readonly MapAreaLoader _mapLoader;

    private readonly WaypointListProjection _pointRows = new();

    private readonly ISettingsStore _settings;

    private readonly IClipboardService _clipboard;

    private readonly CompositeDisposable _lifetime = new();

    private CancellationTokenSource? _calculation;

    private Task _calculationFinished = Task.CompletedTask;

    private readonly ObservableAsPropertyHelper<bool> _canBuild;

    private bool _disposed;

    private RoutingOptions _resultOptions = new();

    private WaypointRow? _selectedPoint;
    private RouteVertexRow? _selectedRouteVertex;

    public string UrlPathSegment => "Карта";

    public IScreen HostScreen
    {
        get;
    }

    public ObservableCollection<WaypointRow> Points => _pointRows.Points;

    public ObservableCollection<RouteAlternative> Alternatives { get; } = [];

    public ObservableCollection<RouteChoice> RouteChoices { get; } = [];

    public ObservableCollection<RouteVertexRow> RouteVertices { get; } = [];

    public ObservableCollection<MapFeature> Features { get; } = [];

    public DataPackageInfo DataInfo => _mapLoader.Info;

    public WaypointRow? SelectedPoint
    {
        get => _selectedPoint;
        set
        {
            if (value is not null)
            {
                SelectedRouteVertex = null;
            }

            for (var index = 0; index < Points.Count; index++)
            {
                var row = Points[index];
                var selected = row.Id == value?.Id;
                if (row.IsSelected != selected)
                {
                    Points[index] = row with
                    {
                        IsSelected = selected
                    };
                }
            }

            this.RaiseAndSetIfChanged(ref _selectedPoint, Points.FirstOrDefault(row => row.Id == value?.Id));
            this.RaisePropertyChanged(nameof(CopyPointText));
        }
    }

    public string CopyPointText => SelectedPoint is { } point ? $"Копировать точку {point.Number}" : "Выберите точку";

    public RouteVertexRow? SelectedRouteVertex
    {
        get => _selectedRouteVertex;
        set
        {
            for (var index = 0; index < RouteVertices.Count; index++)
            {
                var row = RouteVertices[index];
                var selected = row.Number == value?.Number;
                if (row.IsSelected != selected)
                {
                    RouteVertices[index] = row with
                    {
                        IsSelected = selected
                    };
                }
            }

            if (value is not null)
            {
                SelectedPoint = null;
            }

            this.RaiseAndSetIfChanged(ref _selectedRouteVertex,
                RouteVertices.FirstOrDefault(row => row.Number == value?.Number));
            this.RaisePropertyChanged(nameof(CopyRouteVertexText));
        }
    }

    public string CopyRouteVertexText => SelectedRouteVertex is { } vertex
        ? $"Копировать вершину {vertex.Number}"
        : "Выберите вершину";

    public ReactiveCommand<Unit, Unit> CopyRouteVertex
    {
        get;
    }

    [Reactive]
    public RouteAlternative? SelectedRoute
    {
        get; set;
    }

    [Reactive]
    public bool IsBusy
    {
        get; private set;
    }

    [Reactive]
    public int MapVersion
    {
        get; private set;
    }

    [Reactive]
    public int PointCount
    {
        get; private set;
    }

    [Reactive]
    public string Status { get; private set; } = "Нажмите на карту, чтобы добавить точку. Перетаскивайте маркеры.";

    [Reactive]
    public string MapStatus { get; private set; } = "Загрузка офлайн-данных…";

    [Reactive]
    public string LatitudeText { get; set; } = "";

    [Reactive]
    public string LongitudeText { get; set; } = "";

    [Reactive]
    public bool AddMode { get; set; } = true;

    [Reactive]
    public string MetricsText { get; private set; } = "Выберите точки и постройте маршруты.";

    public bool CanBuild => _canBuild.Value;

    public bool HasRoute => SelectedRoute is not null;

    public bool HasAlternatives => Alternatives.Count > 1;

    public bool HasMapNotice => !string.IsNullOrEmpty(MapStatus);

    public ReactiveCommand<GeoCoordinate, Unit> AddPoint
    {
        get; private set;
    }

    public ReactiveCommand<Unit, Unit> AddManual
    {
        get; private set;
    }

    public ReactiveCommand<Unit, Unit> DeletePoint
    {
        get; private set;
    }

    public ReactiveCommand<Guid, Unit> RemovePoint
    {
        get; private set;
    }

    public ReactiveCommand<Guid, Unit> SelectPoint
    {
        get; private set;
    }

    public ReactiveCommand<WaypointReorder, Unit> ReorderPoints
    {
        get; private set;
    }

    public ReactiveCommand<Guid, Unit> ChooseRoute
    {
        get; private set;
    }

    public ReactiveCommand<Unit, Unit> MoveUp
    {
        get; private set;
    }

    public ReactiveCommand<Unit, Unit> MoveDown
    {
        get; private set;
    }

    public ReactiveCommand<Unit, Unit> ClearPoints
    {
        get; private set;
    }

    public ReactiveCommand<Unit, Unit> CopyCoordinates
    {
        get; private set;
    }

    public ReactiveCommand<Unit, Unit> BuildRoutes
    {
        get; private set;
    }

    public ReactiveCommand<Unit, Unit> Cancel
    {
        get; private set;
    }

    public MapViewModel(
        IScreen screen,
        IPlanningSession session,
        IGeodataRepository data,
        ISettingsStore settings,
        IClipboardService clipboard)
    {
        HostScreen = screen;
        _session = session;
        _mapLoader = new MapAreaLoader(data);
        _settings = settings;
        _clipboard = clipboard;

        _canBuild = this.WhenAnyValue(
            x => x.PointCount,
            x => x.IsBusy,
            (count, busy) => count >= 2 && !busy).ToProperty(this, x => x.CanBuild).DisposeWith(_lifetime);
        InitializeCommands();
        CopyRouteVertex = ReactiveCommand.CreateFromTask(async () =>
        {
            if (SelectedRouteVertex is { } vertex)
            {
                await CopyCoordinateAsync(vertex.Coordinate, vertex.Number);
            }
        }, this.WhenAnyValue(x => x.SelectedRouteVertex).Select(vertex => vertex is not null)).DisposeWith(_lifetime);
        SetupSubscriptions();
    }

    [MemberNotNull(
        nameof(AddPoint),
        nameof(AddManual),
        nameof(DeletePoint),
        nameof(RemovePoint),
        nameof(SelectPoint),
        nameof(ReorderPoints),
        nameof(ChooseRoute),
        nameof(MoveUp),
        nameof(MoveDown),
        nameof(ClearPoints),
        nameof(CopyCoordinates),
        nameof(BuildRoutes),
        nameof(Cancel))]
    private void InitializeCommands()
    {
        AddPoint = ReactiveCommand.CreateFromTask<GeoCoordinate>(AddAtAsync).DisposeWith(_lifetime);
        AddManual = ReactiveCommand.CreateFromTask(AddManuallyAsync).DisposeWith(_lifetime);
        RemovePoint = ReactiveCommand.Create<Guid>(id => Change(() => _session.Remove(id))).DisposeWith(_lifetime);
        SelectPoint = ReactiveCommand.Create<Guid>(SelectPointById).DisposeWith(_lifetime);
        DeletePoint = ReactiveCommand.Create(DeleteSelectedPoint).DisposeWith(_lifetime);
        ReorderPoints = ReactiveCommand.CreateFromTask<WaypointReorder>(ReorderByIdAsync).DisposeWith(_lifetime);
        ChooseRoute = ReactiveCommand.Create<Guid>(ChooseRouteById).DisposeWith(_lifetime);
        ClearPoints = ReactiveCommand.Create(() => Change(_session.Clear)).DisposeWith(_lifetime);
        MoveUp = ReactiveCommand.Create(() => Reorder(-1)).DisposeWith(_lifetime);
        MoveDown = ReactiveCommand.Create(() => Reorder(1)).DisposeWith(_lifetime);
        CopyCoordinates = ReactiveCommand.CreateFromTask(CopySelectedCoordinatesAsync,
            this.WhenAnyValue(x => x.SelectedPoint).Select(point => point is not null)).DisposeWith(_lifetime);
        BuildRoutes = ReactiveCommand.CreateFromTask(BuildAsync, this.WhenAnyValue(x => x.CanBuild)).DisposeWith(_lifetime);
        Cancel = ReactiveCommand.Create(() => _calculation?.Cancel()).DisposeWith(_lifetime);
    }

    private void SetupSubscriptions()
    {
        this.WhenAnyValue(x => x.MapStatus)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(HasMapNotice)))
            .DisposeWith(_lifetime);
        this.WhenAnyValue(x => x.SelectedRoute)
            .Subscribe(route =>
            {
                this.RaisePropertyChanged(nameof(HasRoute));
                UpdateRoutePresentation(route);
            })
            .DisposeWith(_lifetime);
    }

    private async Task AddAtAsync(GeoCoordinate coordinate)
    {
        try
        {
            EnsureInBounds(coordinate, "Для этой точки нет данных в офлайн-пакете.");
            await _session.AddValidatedAsync(coordinate, CancellationToken.None);
            RefreshAfterChange();
        }
        catch (PlanningException ex)
        {
            Status = ex.Message;
        }
    }

    private async Task AddManuallyAsync()
    {
        if (!double.TryParse(
                LatitudeText,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var latitude)
            || !double.TryParse(
                LongitudeText,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var longitude))
        {
            Status = "Введите широту и долготу с десятичной точкой.";
            return;
        }

        try
        {
            await AddAtAsync(new GeoCoordinate(latitude, longitude));
        }
        catch (ArgumentOutOfRangeException)
        {
            Status = "Некорректные координаты.";
        }
    }

    private void DeleteSelectedPoint()
    {
        if (SelectedPoint is { } point)
        {
            Change(() => _session.Remove(point.Id));
        }
    }

    private void SelectPointById(Guid id)
    {
        SelectedPoint = Points.FirstOrDefault(point => point.Id == id);
        Status = SelectedPoint is { } point
            ? $"Выбрана точка {point.Number}: {point.Display}."
            : Status;
    }

    private async Task ReorderByIdAsync(WaypointReorder request)
    {
        var source = _pointRows.IndexOf(request.PointId);
        if (source >= 0 && source != request.TargetIndex
            && request.TargetIndex >= 0 && request.TargetIndex < Points.Count)
        {
            var rebuild = SelectedRoute is not null || IsBusy;
            var previousCalculation = _calculationFinished;
            Change(() => _session.Reorder(source, request.TargetIndex));
            if (rebuild)
            {
                // An in-flight calculation must finish cancelling before its replacement
                // starts; otherwise a second drop could leave the route empty.
                await previousCalculation;
                if (!_disposed && CanBuild)
                {
                    await BuildAsync();
                }
            }
        }
    }

    private void ChooseRouteById(Guid id)
    {
        SelectedRoute = Alternatives.FirstOrDefault(route => route.Id == id);
        if (SelectedRoute is { } route)
        {
            Status = $"На карте: {route.Name}, {route.Metrics.LengthMetres:N0} м.";
        }
    }

    private async Task CopySelectedCoordinatesAsync()
    {
        if (SelectedPoint is not { } point)
        {
            return;
        }

        await CopyCoordinateAsync(point.Coordinate, point.Number);
    }

    private async Task CopyCoordinateAsync(GeoCoordinate coordinate, int number)
    {
        try
        {
            await _clipboard.SetTextAsync(coordinate.ToString());
            Status = $"Скопирована точка {number}: {coordinate}.";
        }
        catch (Exception ex)
        {
            Status = $"Не удалось скопировать координаты: {ex.Message}";
        }
    }

    private void UpdateRoutePresentation(RouteAlternative? route)
    {
        RouteChoices.Clear();
        RouteVertices.Clear();
        SelectedRouteVertex = null;
        var hasAlternatives = Alternatives.Count > 1;
        foreach (var alternative in Alternatives)
        {
            RouteChoices.Add(new RouteChoice(
                alternative.Id,
                hasAlternatives ? alternative.Name : "Маршрут",
                $"{alternative.Metrics.LengthMetres:N0} м",
                alternative.Id == route?.Id));
        }

        if (route is not null)
        {
            var expandedGeometry = ExpandWithTransitionPoints(route);
            var coordinates = expandedGeometry
                .Where((coordinate, index) => index == 0 || coordinate != expandedGeometry[index - 1])
                .ToArray();
            for (var index = 0; index < coordinates.Length; index++)
            {
                var coordinate = coordinates[index];
                var isEndpoint = index == 0 || index == coordinates.Length - 1;
                var isWaypoint = Points.Any(point => point.Coordinate.DistanceTo(coordinate) <= 1);
                var isTransition = route.TransitionPoints?.Any(point => point.DistanceTo(coordinate) <= 1) == true;
                var isTurn = index > 0 && index < coordinates.Length - 1 && IsSignificantTurn(
                    coordinates[index - 1], coordinate, coordinates[index + 1]);
                if ((isEndpoint || isWaypoint || isTransition || isTurn)
                    && (RouteVertices.Count == 0 || RouteVertices[^1].Coordinate != coordinate))
                {
                    var kind = isWaypoint ? RouteVertexKind.UserWaypoint
                        : isTransition ? RouteVertexKind.Transition
                        : isEndpoint ? RouteVertexKind.Endpoint
                        : RouteVertexKind.Turn;
                    RouteVertices.Add(new RouteVertexRow(RouteVertices.Count + 1, coordinate, kind));
                }
            }
        }

        this.RaisePropertyChanged(nameof(HasAlternatives));

        MetricsText = route is null
            ? "Актуальный маршрут не выбран."
            : RouteMetricsFormatter.Format(route, _resultOptions);
    }

    private static IReadOnlyList<GeoCoordinate> ExpandWithTransitionPoints(RouteAlternative route)
    {
        if (route.Geometry.Count < 2 || route.TransitionPoints is not { Count: > 0 } transitions)
        {
            return route.Geometry;
        }

        var expanded = new List<GeoCoordinate> { route.Geometry[0] };
        for (var index = 1; index < route.Geometry.Count; index++)
        {
            var start = route.Geometry[index - 1];
            var end = route.Geometry[index];
            foreach (var transition in transitions
                .Select(point => (Point: point, Position: PositionOnSegment(start, end, point)))
                .Where(item => item.Position.DistanceMetres <= 2
                    && item.Position.Fraction > 0
                    && item.Position.Fraction < 1)
                .OrderBy(item => item.Position.Fraction))
            {
                if (expanded[^1] != transition.Point)
                {
                    expanded.Add(transition.Point);
                }
            }

            if (expanded[^1] != end)
            {
                expanded.Add(end);
            }
        }

        return expanded;
    }

    private static (double Fraction, double DistanceMetres) PositionOnSegment(
        GeoCoordinate start,
        GeoCoordinate end,
        GeoCoordinate point)
    {
        var latitudeScale = Math.Cos((start.Latitude + end.Latitude) / 2 * Math.PI / 180) * 111_320;
        const double latitudeMetresPerDegree = 111_132;
        var dx = (end.Longitude - start.Longitude) * latitudeScale;
        var dy = (end.Latitude - start.Latitude) * latitudeMetresPerDegree;
        var px = (point.Longitude - start.Longitude) * latitudeScale;
        var py = (point.Latitude - start.Latitude) * latitudeMetresPerDegree;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared == 0)
        {
            return (0, Math.Sqrt(px * px + py * py));
        }

        var fraction = Math.Clamp((px * dx + py * dy) / lengthSquared, 0, 1);
        var offsetX = px - fraction * dx;
        var offsetY = py - fraction * dy;
        return (fraction, Math.Sqrt(offsetX * offsetX + offsetY * offsetY));
    }

    private static bool IsSignificantTurn(GeoCoordinate previous, GeoCoordinate current, GeoCoordinate next)
    {
        var firstLength = previous.DistanceTo(current);
        var secondLength = current.DistanceTo(next);
        if (firstLength < 15 || secondLength < 15)
        {
            return false;
        }

        static double Bearing(GeoCoordinate from, GeoCoordinate to)
        {
            var latitude1 = from.Latitude * Math.PI / 180;
            var latitude2 = to.Latitude * Math.PI / 180;
            var longitudeDelta = (to.Longitude - from.Longitude) * Math.PI / 180;
            return Math.Atan2(
                Math.Sin(longitudeDelta) * Math.Cos(latitude2),
                Math.Cos(latitude1) * Math.Sin(latitude2)
                    - Math.Sin(latitude1) * Math.Cos(latitude2) * Math.Cos(longitudeDelta));
        }

        var difference = Math.Abs(Bearing(previous, current) - Bearing(current, next));
        difference = Math.Min(difference, 2 * Math.PI - difference);
        return difference >= 25 * Math.PI / 180;
    }

    private void EnsureInBounds(GeoCoordinate coordinate, string errorMessage)
    {
        if (!_mapLoader.Info.Bounds.Contains(coordinate))
        {
            throw new PlanningException(errorMessage);
        }
    }

    public async Task LoadMapAreaAsync(GeoBounds bounds, CancellationToken ct, double metresPerPixel = 0)
    {
        var result = await _mapLoader.LoadLatestAsync(bounds, metresPerPixel, ct);
        if (_disposed || result is null || !_mapLoader.IsCurrent(result))
        {
            return;
        }

        switch (result)
        {
            case MapAreaLoadResult.Failed failed:
                MapStatus = $"Ошибка загрузки карты: {failed.Message}";
                break;

            case MapAreaLoadResult.Loaded loaded:
                Features.Clear();
                foreach (var feature in loaded.Features)
                {
                    Features.Add(feature);
                }

                MapVersion++;
                MapStatus = loaded.Features.Count > 0
                    ? ""
                    : "Для этой области нет данных в офлайн-пакете.";
                break;

            default:
                throw new InvalidOperationException("Неизвестный результат загрузки карты.");
        }
    }

    public async Task MovePointAsync(Guid id, GeoCoordinate coordinate)
    {
        try
        {
            var selected = SelectedPoint?.Id;
            EnsureInBounds(coordinate, "Для этой точки нет данных в офлайн-пакете.");
            await _session.MoveValidatedAsync(id, coordinate, CancellationToken.None);
            RefreshAfterChange(selected ?? id);
        }
        catch (PlanningException ex)
        {
            Status = ex.Message;
        }
    }

    private void Reorder(int direction)
    {
        if (SelectedPoint is not { } p)
        {
            return;
        }

        var oldIndex = _pointRows.IndexOf(p.Id);
        var newIndex = oldIndex + direction;
        if (newIndex >= 0 && newIndex < Points.Count)
        {
            Change(() => _session.Reorder(oldIndex, newIndex));
        }
    }

    private void Change(Action operation)
    {
        try
        {
            var selected = SelectedPoint?.Id;
            operation();
            RefreshAfterChange(selected);
        }
        catch (PlanningException ex)
        {
            Status = ex.Message;
        }
    }

    private void RefreshAfterChange(Guid? selected = null)
    {
        _calculation?.Cancel();
        Alternatives.Clear();
        RouteChoices.Clear();
        SelectedRoute = null;
        SelectedPoint = _pointRows.Refresh(_session.Points, selected);
        PointCount = Points.Count;
        Status = "Точки изменены. Для актуальных результатов постройте маршруты.";
    }

    private async Task BuildAsync()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _calculationFinished = finished.Task;
        IsBusy = true;
        Alternatives.Clear();
        RouteChoices.Clear();
        SelectedRoute = null;
        Status = "Анализ местности и поиск маршрутов…";
        using var cts = new CancellationTokenSource();
        _calculation = cts;
        var inputRevision = _session.Revision;
        try
        {
            _resultOptions = _settings.Load();
            var routes = await _session.BuildAsync(_resultOptions, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_calculation, cts) || inputRevision != _session.Revision)
            {
                return;
            }

            foreach (var route in routes)
            {
                Alternatives.Add(route);
            }

            SelectedRoute = Alternatives.FirstOrDefault();
            Status = routes.Count > 0 ? $"Найдено различных маршрутов: {routes.Count}. Разрешённость строительства не подтверждена." : "Допустимые маршруты не найдены.";
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_calculation, cts) && inputRevision == _session.Revision)
            {
                Status = "Расчёт отменён. Актуальных маршрутов нет.";
            }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_calculation, cts) && inputRevision == _session.Revision)
            {
                Status = $"Не удалось построить маршрут: {ex.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_calculation, cts))
            {
                _calculation = null;
                IsBusy = false;
            }

            finished.TrySetResult();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _calculation?.Cancel();
        _mapLoader.Invalidate();
        _lifetime.Dispose();
    }
}
