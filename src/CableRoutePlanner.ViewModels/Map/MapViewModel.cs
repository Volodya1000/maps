using System.Collections.ObjectModel;
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

    private readonly IGeodataRepository _data;

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

    public ObservableCollection<WaypointRow> Points { get; } = [];

    public ObservableCollection<RouteAlternative> Alternatives { get; } = [];

    public ObservableCollection<RouteChoice> RouteChoices { get; } = [];

    public ObservableCollection<MapFeature> Features { get; } = [];

    public DataPackageInfo DataInfo => _data.Info;

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

    private long _mapRequest;

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
        get;
    }

    public ReactiveCommand<Unit, Unit> AddManual
    {
        get;
    }

    public ReactiveCommand<Unit, Unit> DeletePoint
    {
        get;
    }

    public ReactiveCommand<Guid, Unit> RemovePoint
    {
        get;
    }

    public ReactiveCommand<Guid, Unit> SelectPoint
    {
        get;
    }

    public ReactiveCommand<WaypointReorder, Unit> ReorderPoints
    {
        get;
    }

    public ReactiveCommand<Guid, Unit> ChooseRoute
    {
        get;
    }

    public ReactiveCommand<Unit, Unit> MoveUp
    {
        get;
    }

    public ReactiveCommand<Unit, Unit> MoveDown
    {
        get;
    }

    public ReactiveCommand<Unit, Unit> ClearPoints
    {
        get;
    }

    public ReactiveCommand<Unit, Unit> CopyCoordinates
    {
        get;
    }

    public ReactiveCommand<Unit, Unit> BuildRoutes
    {
        get;
    }

    public ReactiveCommand<Unit, Unit> Cancel
    {
        get;
    }

    public ReactiveCommand<Unit, Unit> Request3D
    {
        get;
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
        _data = data;
        _settings = settings;
        _clipboard = clipboard;
        _canBuild = this.WhenAnyValue(
            x => x.PointCount,
            x => x.IsBusy,
            (count, busy) => count >= 2 && !busy).ToProperty(this, x => x.CanBuild).DisposeWith(_lifetime);
        AddPoint = ReactiveCommand.Create<GeoCoordinate>(coordinate => Change(() =>
        {
            if (!_data.Info.Bounds.Contains(coordinate))
            {
                throw new PlanningException("Для этой точки нет данных в офлайн-пакете.");
            }

            _session.Add(coordinate);
        })).DisposeWith(_lifetime);
        AddManual = ReactiveCommand.Create(() =>
        {
            if (!double.TryParse(
                LatitudeText,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var lat) || !double.TryParse(
                LongitudeText,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var lon))
            {
                Status = "Введите широту и долготу с десятичной точкой.";
                return;
            }

            try
            {
                AddPoint.Execute(new(lat, lon)).Subscribe().Dispose();
            }
            catch (ArgumentOutOfRangeException)
            {
                Status = "Некорректные координаты.";
            }
        }).DisposeWith(_lifetime);
        RemovePoint = ReactiveCommand.Create<Guid>(id => Change(() => _session.Remove(id))).DisposeWith(_lifetime);
        SelectPoint = ReactiveCommand.Create<Guid>(id =>
        {
            SelectedPoint = Points.FirstOrDefault(p => p.Id == id);
        }).DisposeWith(_lifetime);
        DeletePoint = ReactiveCommand.Create(() =>
        {
            if (SelectedPoint is { } p)
            {
                Change(() => _session.Remove(p.Id));
            }
        }).DisposeWith(_lifetime);
        ReorderPoints = ReactiveCommand.Create<WaypointReorder>(request =>
        {
            var source = _session.Points.ToList().FindIndex(p => p.Id == request.PointId);
            if (source >= 0 && source != request.TargetIndex)
            {
                Change(() => _session.Reorder(source, request.TargetIndex));
            }
        }).DisposeWith(_lifetime);
        ChooseRoute = ReactiveCommand.Create<Guid>(id =>
        {
            SelectedRoute = Alternatives.FirstOrDefault(route => route.Id == id);
            if (SelectedRoute is not null)
            {
                Status = $"На карте: {SelectedRoute.Name}, {SelectedRoute.Metrics.LengthMetres:N0} м.";
            }
        }).DisposeWith(_lifetime);
        ClearPoints = ReactiveCommand.Create(() => Change(_session.Clear)).DisposeWith(_lifetime);
        MoveUp = ReactiveCommand.Create(() => Reorder(-1)).DisposeWith(_lifetime);
        MoveDown = ReactiveCommand.Create(() => Reorder(1)).DisposeWith(_lifetime);
        CopyCoordinates = ReactiveCommand.CreateFromTask(async () =>
        {
            if (SelectedPoint is not { } p)
            {
                return;
            }

            try
            {
                await _clipboard.SetTextAsync(p.Coordinate.ToString());
                Status = "Координаты скопированы.";
            }
            catch (Exception ex)
            {
                Status = $"Не удалось скопировать координаты: {ex.Message}";
            }
        }).DisposeWith(_lifetime);
        BuildRoutes = ReactiveCommand.CreateFromTask(BuildAsync, this.WhenAnyValue(x => x.CanBuild)).DisposeWith(_lifetime);
        Cancel = ReactiveCommand.Create(() => _calculation?.Cancel()).DisposeWith(_lifetime);
        Request3D = ReactiveCommand.Create(() =>
        {
            Status = "3D недоступен: интеграция MapLibre и DEM ещё не прошла проверку. Открыта 2D-карта.";
        }).DisposeWith(_lifetime);
        this.WhenAnyValue(x => x.SelectedRoute).Subscribe(route =>
        {
            RouteChoices.Clear();
            foreach (var alternative in Alternatives)
            {
                RouteChoices.Add(
                    new RouteChoice(
                        alternative.Id,
                        alternative.Name,
                        $"{alternative.Metrics.LengthMetres:N0} м",
                        alternative.Id == route?.Id));
            }

            if (route is null)
            {
                MetricsText = "Актуальный маршрут не выбран.";
                return;
            }

            var m = route.Metrics;
            MetricsText = string.Join(
                Environment.NewLine,
                $"Длина: {m.LengthMetres:N0} м",
                $"Кабель с запасом {_resultOptions.ReserveFraction:P0}: " + $"{m.CableLengthMetres(_resultOptions.ReserveFraction):N0} м",
                $"Лес: {m.ForestMetres:N0} м · поле: {m.FieldMetres:N0} м",
                $"Пересечённых объектов дорог: {m.RoadCrossings}",
                $"Разрешённых водных переходов: {m.ApprovedWaterCrossings}",
                $"Взвешенная стоимость: {m.WeightedCost:N0} усл. м",
                "Высотные характеристики: нет данных DEM",
                "Требуются обследование и согласование.");
        }).DisposeWith(_lifetime);
    }

    public async Task LoadMapAreaAsync(GeoBounds bounds, CancellationToken ct, double metresPerPixel = 0)
    {
        var request = Interlocked.Increment(ref _mapRequest);
        try
        {
            var features = await _data.QueryMapAsync(bounds, metresPerPixel, ct);
            ct.ThrowIfCancellationRequested();
            if (request != _mapRequest || _disposed)
            {
                return;
            }

            Features.Clear();
            foreach (var feature in features)
            {
                Features.Add(feature);
            }

            MapVersion++;
            MapStatus = features.Count > 0 ? $"Офлайн: {_data.Info.RegionId} · OSM · без DEM и WorldCover" : "Для этой области нет данных в офлайн-пакете.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (request == _mapRequest)
            {
                MapStatus = $"Ошибка загрузки карты: {ex.Message}";
            }
        }
    }

    public void MovePoint(Guid id, GeoCoordinate coordinate) => Change(() =>
    {
        if (!_data.Info.Bounds.Contains(coordinate))
        {
            throw new PlanningException("Точка за пределами офлайн-пакета.");
        }

        _session.Move(id, coordinate);
    });

    private void Reorder(int direction)
    {
        if (SelectedPoint is not { } p)
        {
            return;
        }

        var oldIndex = Points.IndexOf(p);
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
            Points.Clear();
            foreach (var (p, index) in _session.Points.Select((p, i) => (p, i)))
                Points.Add(new(p.Id, index + 1, p.Coordinate));
            PointCount = Points.Count;
            SelectedPoint = Points.FirstOrDefault(p => p.Id == selected) ?? Points.LastOrDefault();
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
        _lifetime.Dispose();
    }
}
