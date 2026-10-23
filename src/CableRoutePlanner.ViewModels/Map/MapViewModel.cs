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

public sealed record WaypointRow(Guid Id, int Number, GeoCoordinate Coordinate)
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

    private readonly ObservableAsPropertyHelper<bool> _canBuild;

    private bool _disposed;

    private RoutingOptions _resultOptions = new();

    public string UrlPathSegment => "Карта";

    public IScreen HostScreen
    {
        get;
    }

    public ObservableCollection<WaypointRow> Points => _pointRows.Points;

    public ObservableCollection<RouteAlternative> Alternatives { get; } = [];

    public ObservableCollection<RouteChoice> RouteChoices { get; } = [];

    public ObservableCollection<MapFeature> Features { get; } = [];

    public DataPackageInfo DataInfo => _mapLoader.Info;

    [Reactive]
    public WaypointRow? SelectedPoint
    {
        get; set;
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

    public ReactiveCommand<GeoCoordinate, Unit> AddPoint
    {
        get; private set;
    };

    public ReactiveCommand<Unit, Unit> AddManual
    {
        get; private set;
    };

    public ReactiveCommand<Unit, Unit> DeletePoint
    {
        get; private set;
    };

    public ReactiveCommand<Guid, Unit> RemovePoint
    {
        get; private set;
    };

    public ReactiveCommand<Guid, Unit> SelectPoint
    {
        get; private set;
    };

    public ReactiveCommand<WaypointReorder, Unit> ReorderPoints
    {
        get; private set;
    };

    public ReactiveCommand<Guid, Unit> ChooseRoute
    {
        get; private set;
    };

    public ReactiveCommand<Unit, Unit> MoveUp
    {
        get; private set;
    };

    public ReactiveCommand<Unit, Unit> MoveDown
    {
        get; private set;
    };

    public ReactiveCommand<Unit, Unit> ClearPoints
    {
        get; private set;
    };

    public ReactiveCommand<Unit, Unit> CopyCoordinates
    {
        get; private set;
    };

    public ReactiveCommand<Unit, Unit> BuildRoutes
    {
        get; private set;
    };

    public ReactiveCommand<Unit, Unit> Cancel
    {
        get; private set;
    };

    public ReactiveCommand<Unit, Unit> Request3D
    {
        get; private set;
    };

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
        nameof(Cancel),
        nameof(Request3D))]
    private void InitializeCommands()
    {
        AddPoint = ReactiveCommand.Create<GeoCoordinate>(AddAt).DisposeWith(_lifetime);
        AddManual = ReactiveCommand.Create(AddManually).DisposeWith(_lifetime);
        RemovePoint = ReactiveCommand.Create<Guid>(id => Change(() => _session.Remove(id))).DisposeWith(_lifetime);
        SelectPoint = ReactiveCommand.Create<Guid>(id =>
            SelectedPoint = Points.FirstOrDefault(point => point.Id == id)).DisposeWith(_lifetime);
        DeletePoint = ReactiveCommand.Create(DeleteSelectedPoint).DisposeWith(_lifetime);
        ReorderPoints = ReactiveCommand.Create<WaypointReorder>(ReorderById).DisposeWith(_lifetime);
        ChooseRoute = ReactiveCommand.Create<Guid>(ChooseRouteById).DisposeWith(_lifetime);
        ClearPoints = ReactiveCommand.Create(() => Change(_session.Clear)).DisposeWith(_lifetime);
        MoveUp = ReactiveCommand.Create(() => Reorder(-1)).DisposeWith(_lifetime);
        MoveDown = ReactiveCommand.Create(() => Reorder(1)).DisposeWith(_lifetime);
        CopyCoordinates = ReactiveCommand.CreateFromTask(CopySelectedCoordinatesAsync).DisposeWith(_lifetime);
        BuildRoutes = ReactiveCommand.CreateFromTask(BuildAsync, this.WhenAnyValue(x => x.CanBuild)).DisposeWith(_lifetime);
        Cancel = ReactiveCommand.Create(() => _calculation?.Cancel()).DisposeWith(_lifetime);
        Request3D = ReactiveCommand.Create(() =>
        {
            Status = "3D недоступен: интеграция MapLibre и DEM ещё не прошла проверку. Открыта 2D-карта.";
        }).DisposeWith(_lifetime);
    }

    private void SetupSubscriptions()
    {
        this.WhenAnyValue(x => x.SelectedRoute)
            .Subscribe(UpdateRoutePresentation)
            .DisposeWith(_lifetime);
    }

    private void AddAt(GeoCoordinate coordinate) => Change(() =>
    {
        EnsureInBounds(coordinate, "Для этой точки нет данных в офлайн-пакете.");
        _session.Add(coordinate);
    });

    private void AddManually()
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
            AddAt(new GeoCoordinate(latitude, longitude));
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

    private void ReorderById(WaypointReorder request)
    {
        var source = _pointRows.IndexOf(request.PointId);
        if (source >= 0 && source != request.TargetIndex)
        {
            Change(() => _session.Reorder(source, request.TargetIndex));
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

        try
        {
            await _clipboard.SetTextAsync(point.Coordinate.ToString());
            Status = "Координаты скопированы.";
        }
        catch (Exception ex)
        {
            Status = $"Не удалось скопировать координаты: {ex.Message}";
        }
    }

    private void UpdateRoutePresentation(RouteAlternative? route)
    {
        RouteChoices.Clear();
        foreach (var alternative in Alternatives)
        {
            RouteChoices.Add(new RouteChoice(
                alternative.Id,
                alternative.Name,
                $"{alternative.Metrics.LengthMetres:N0} м",
                alternative.Id == route?.Id));
        }

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
                    ? $"Офлайн: {_mapLoader.Info.RegionId} · OSM · без DEM и WorldCover"
                    : "Для этой области нет данных в офлайн-пакете.";
                break;

            default:
                throw new InvalidOperationException("Неизвестный результат загрузки карты.");
        }
    }

    public void MovePoint(Guid id, GeoCoordinate coordinate) => Change(() =>
    {
        EnsureInBounds(coordinate, "Точка за пределами офлайн-пакета.");

        _session.Move(id, coordinate);
    });

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
            _calculation?.Cancel();
            Alternatives.Clear();
            RouteChoices.Clear();
            SelectedRoute = null;
            SelectedPoint = _pointRows.Refresh(_session.Points, selected);
            PointCount = Points.Count;
            Status = "Точки изменены. Для актуальных результатов постройте маршруты.";
        }
        catch (PlanningException ex)
        {
            Status = ex.Message;
        }
    }

    private async Task BuildAsync()
    {
        IsBusy = true;
        Alternatives.Clear();
        RouteChoices.Clear();
        SelectedRoute = null;
        Status = "Анализ местности и поиск маршрутов…";
        using var cts = new CancellationTokenSource();
        _calculation = cts;
        try
        {
            _resultOptions = _settings.Load();
            var routes = await _session.BuildAsync(_resultOptions, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            foreach (var route in routes)
            {
                Alternatives.Add(route);
            }

            SelectedRoute = Alternatives.FirstOrDefault();
            Status = routes.Count > 0 ? $"Найдено различных маршрутов: {routes.Count}. Разрешённость строительства не подтверждена." : "Допустимые маршруты не найдены.";
        }
        catch (OperationCanceledException)
        {
            Status = "Расчёт отменён. Актуальных маршрутов нет.";
        }
        catch (Exception ex)
        {
            Status = $"Не удалось построить маршрут: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_calculation, cts))
            {
                _calculation = null;
            }

            IsBusy = false;
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
