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

public sealed record RouteVertexRow(
    int Number,
    GeoCoordinate Coordinate,
    RouteVertexKind Kind = RouteVertexKind.Turn,
    bool IsSelected = false,
    int LegIndex = 0,
    int LegSubIndex = 0)
{
    public string Display => Coordinate.ToString();

    public bool IsChild => LegSubIndex > 0;

    public string KindText => Kind == RouteVertexKind.Transition ? "Мост OSM — требует проверки" : "";

    // Number stays an internal, unique selection key; DisplayLabel is the
    // stable, human-readable position between user waypoints.
    public string DisplayLabel => LegIndex == 0
        ? Number.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : LegSubIndex == 0
            ? LegIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{LegIndex}.{LegSubIndex}");
}

public sealed record WaypointReorder(Guid PointId, int TargetIndex);

public sealed record RouteChoice(Guid Id, string Name, string LengthText, bool IsSelected);

public sealed class MapViewModel : ReactiveObject, IRoutableViewModel, IDisposable
{
    private readonly IPlanningSession _session;

    private readonly IMapAreaLoader _mapLoader;

    private readonly WaypointListProjection _pointRows = new();

    private readonly ISettingsStore _settings;

    private readonly IClipboardService _clipboard;

    private readonly CompositeDisposable _lifetime = new();

    private CancellationTokenSource? _calculation;

    private CancellationTokenSource? _pointMove;

    private Task _calculationFinished = Task.CompletedTask;

    private readonly ObservableAsPropertyHelper<bool> _canBuild;

    private bool _disposed;

    private RoutingOptions _resultOptions = new();

    private WaypointRow? _selectedPoint;
    private RouteVertexRow? _selectedRouteVertex;

    public string UrlPathSegment => "Карта";

    public IScreen HostScreen { get; }

    public ObservableCollection<WaypointRow> Points => _pointRows.Points;

    public ObservableCollection<RouteAlternative> Alternatives { get; } = [];

    public ObservableCollection<RouteChoice> RouteChoices { get; } = [];

    public ObservableCollection<RouteVertexRow> RouteVertices { get; } = [];

    public IReadOnlyList<MapFeature> Features { get; private set; } = Array.Empty<MapFeature>();

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

            WaypointRow? selected = RowSelection.Apply(Points, value,
                row => row.Id, row => row.IsSelected, (row, isSelected) => row with { IsSelected = isSelected });
            this.RaiseAndSetIfChanged(ref _selectedPoint, selected);
            this.RaisePropertyChanged(nameof(CopyPointText));
        }
    }

    public string CopyPointText => SelectedPoint is { } point ? $"Копировать точку {point.Number}" : "Выберите точку";

    public RouteVertexRow? SelectedRouteVertex
    {
        get => _selectedRouteVertex;
        set
        {
            RouteVertexRow? selected = RowSelection.Apply(RouteVertices, value,
                row => row.Number, row => row.IsSelected, (row, isSelected) => row with { IsSelected = isSelected });

            if (value is not null)
            {
                SelectedPoint = null;
            }

            this.RaiseAndSetIfChanged(ref _selectedRouteVertex,
                selected);
            this.RaisePropertyChanged(nameof(CopyRouteVertexText));
        }
    }

    public string CopyRouteVertexText => SelectedRouteVertex is { } vertex
        ? $"Копировать вершину {vertex.DisplayLabel}"
        : "Выберите вершину";

    public ReactiveCommand<Unit, Unit> CopyRouteVertex { get; }

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
        IMapAreaLoader mapLoader,
        ISettingsStore settings,
        IClipboardService clipboard)
    {
        HostScreen = screen;
        _session = session;
        _mapLoader = mapLoader;
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
                await CopyCoordinateAsync(vertex.Coordinate, vertex.DisplayLabel);
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
            await _session.AddAsync(coordinate, CancellationToken.None);
            RefreshAfterChange();
        }
        catch (PlanningException ex)
        {
            Status = ex.Message;
        }
    }

    private async Task AddManuallyAsync()
    {
        if (!GeoCoordinateParser.TryParse(LatitudeText, LongitudeText, out GeoCoordinate coordinate, out string error))
        {
            Status = error;
            return;
        }

        await AddAtAsync(coordinate);
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
        int source = _pointRows.IndexOf(request.PointId);
        if (source >= 0 && source != request.TargetIndex
            && request.TargetIndex >= 0 && request.TargetIndex < Points.Count)
        {
            bool rebuild = SelectedRoute is not null || IsBusy;
            Task previousCalculation = _calculationFinished;
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

        await CopyCoordinateAsync(point.Coordinate, point.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task CopyCoordinateAsync(GeoCoordinate coordinate, string number)
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
        bool hasAlternatives = Alternatives.Count > 1;
        foreach (RouteAlternative alternative in Alternatives)
        {
            RouteChoices.Add(new RouteChoice(
                alternative.Id,
                hasAlternatives ? alternative.Name : "Маршрут",
                $"{alternative.Metrics.LengthMetres:N0} м",
                alternative.Id == route?.Id));
        }

        if (route is not null)
        {
            foreach (RouteVertexRow vertex in RouteVertexProjection.Project(route, Points.Select(point => point.Coordinate).ToArray()))
            {
                RouteVertices.Add(vertex);
            }
        }

        this.RaisePropertyChanged(nameof(HasAlternatives));

        MetricsText = route is null
            ? "Актуальный маршрут не выбран."
            : RouteMetricsFormatter.Format(route, _resultOptions);
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
        MapAreaLoadResult? result = await _mapLoader.LoadLatestAsync(bounds, metresPerPixel, ct);
        if (_disposed || result is null || !_mapLoader.IsCurrent(result))
        {
            return;
        }

        switch (result)
        {
            case MapAreaFailed failed:
                MapStatus = $"Ошибка загрузки карты: {failed.Message}";
                break;

            case MapAreaLoaded loaded:
                Features = Array.AsReadOnly(loaded.Features.ToArray());
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
        if (_disposed)
        {
            return;
        }

        var current = new CancellationTokenSource();
        CancellationTokenSource? previous = _pointMove;
        _pointMove = current;
        previous?.Cancel();
        try
        {
            Guid? selected = SelectedPoint?.Id;
            EnsureInBounds(coordinate, "Для этой точки нет данных в офлайн-пакете.");
            await _session.MoveAsync(id, coordinate, current.Token);
            current.Token.ThrowIfCancellationRequested();
            RefreshAfterChange(selected ?? id);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            System.Diagnostics.Debug.WriteLine("Устаревшее перемещение точки отменено.");
        }
        catch (PlanningException ex)
        {
            Status = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_pointMove, current))
            {
                _pointMove = null;
            }

            current.Dispose();
        }
    }

    public void ReportUnexpectedError(Exception error)
    {
        if (_disposed)
        {
            return;
        }

        Console.Error.WriteLine(error);
        Status = "Операция не выполнена из-за внутренней ошибки. Повторите действие.";
    }

    private void Reorder(int direction)
    {
        if (SelectedPoint is not { } p)
        {
            return;
        }

        int oldIndex = _pointRows.IndexOf(p.Id);
        int newIndex = oldIndex + direction;
        if (newIndex >= 0 && newIndex < Points.Count)
        {
            Change(() => _session.Reorder(oldIndex, newIndex));
        }
    }

    private void Change(Action operation)
    {
        try
        {
            Guid? selected = SelectedPoint?.Id;
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
        _pointMove?.Cancel();
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
        long inputRevision = _session.Revision;
        try
        {
            _resultOptions = _settings.Load();
            IReadOnlyList<RouteAlternative> routes = await _session.BuildAsync(_resultOptions, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_calculation, cts) || inputRevision != _session.Revision)
            {
                return;
            }

            foreach (RouteAlternative route in routes)
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
