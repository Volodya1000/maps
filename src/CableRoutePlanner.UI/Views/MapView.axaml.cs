using System.Collections.Specialized;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using CableRoutePlanner.Domain;
using CableRoutePlanner.UI.MapAdapters;
using CableRoutePlanner.ViewModels;
using ReactiveUI;

namespace CableRoutePlanner.UI.Views;

public partial class MapView : ReactiveUserControl<MapViewModel>
{
    private const double PanelSwipeThreshold = 36;

    private readonly Func<Mapsui.UI.Avalonia.MapControl, GeoBounds, IMapAdapter> _adapterFactory;

    private IMapAdapter? _activeAdapter;
    private IPointer? _panelPointer;
    private Point _panelPointerStart;
    private bool _panelExpanded = true;

    public MapView(Func<Mapsui.UI.Avalonia.MapControl, GeoBounds, IMapAdapter> adapterFactory)
    {
        _adapterFactory = adapterFactory;
        InitializeComponent();
        this.WhenActivated(Activate);
        SizeChanged += OnSizeChanged;
    }

    private void Activate(CompositeDisposable lifetime)
    {
        var viewModel = ViewModel!;
        var adapter = _adapterFactory(MapControl, viewModel.DataInfo.Bounds)
            .DisposeWith(lifetime);
        _activeAdapter = adapter;

        BindCollections(viewModel, adapter, lifetime);
        BindMapGestures(viewModel, adapter, lifetime);
        BindMapState(viewModel, adapter, lifetime);
        BindViewport(viewModel, adapter, lifetime);
        BindButtons();
        void NorthAngleChanged(double angle) => NorthArrow.RenderTransform = new RotateTransform(angle);
        adapter.NorthIndicatorRotationChanged += NorthAngleChanged;
        Disposable.Create(() => adapter.NorthIndicatorRotationChanged -= NorthAngleChanged)
            .DisposeWith(lifetime);

        adapter.InitializeViewport();
        adapter.SetPoints(viewModel.Points.ToArray());
        adapter.SetRoutes(viewModel.Alternatives.ToArray(), viewModel.SelectedRoute?.Id);
        adapter.SetRouteVertices(viewModel.RouteVertices.ToArray());
        Disposable.Create(UnbindButtons)
            .DisposeWith(lifetime);
    }

    private static void BindMapGestures(
        MapViewModel viewModel,
        IMapAdapter adapter,
        CompositeDisposable lifetime)
    {
        var active = true;
        void PointAdded(GeoCoordinate point) =>
            viewModel.AddPoint.Execute(point)
                .Subscribe(_ => { }, viewModel.ReportUnexpectedError)
                .DisposeWith(lifetime);

        async void PointMoved(Guid id, GeoCoordinate point)
        {
            try
            {
                await viewModel.MovePointAsync(id, point);
            }
            catch (Exception error)
            {
                viewModel.ReportUnexpectedError(error);
            }
            finally
            {
                if (active)
                {
                    adapter.SetPoints(viewModel.Points.ToArray());
                }
            }
        }

        void WaypointSelected(Guid id) => viewModel.SelectPoint.Execute(id)
            .Subscribe(_ => { }, viewModel.ReportUnexpectedError)
            .DisposeWith(lifetime);

        void RouteVertexSelected(int number) => viewModel.SelectedRouteVertex =
            viewModel.RouteVertices.FirstOrDefault(vertex => vertex.Number == number);

        adapter.PointAdded += PointAdded;
        adapter.PointMoved += PointMoved;
        adapter.WaypointSelected += WaypointSelected;
        adapter.RouteVertexSelected += RouteVertexSelected;
        Disposable.Create(() =>
        {
            active = false;
            adapter.PointAdded -= PointAdded;
            adapter.PointMoved -= PointMoved;
            adapter.WaypointSelected -= WaypointSelected;
            adapter.RouteVertexSelected -= RouteVertexSelected;
        }).DisposeWith(lifetime);
    }

    private static void BindCollections(
        MapViewModel viewModel,
        IMapAdapter adapter,
        CompositeDisposable lifetime)
    {
        void PointsChanged(object? sender, NotifyCollectionChangedEventArgs args) =>
            adapter.SetPoints(viewModel.Points.ToArray());

        void RoutesChanged(object? sender, NotifyCollectionChangedEventArgs args) =>
            adapter.SetRoutes(viewModel.Alternatives.ToArray(), viewModel.SelectedRoute?.Id);

        void VerticesChanged(object? sender, NotifyCollectionChangedEventArgs args) =>
            adapter.SetRouteVertices(viewModel.RouteVertices.ToArray());

        viewModel.Points.CollectionChanged += PointsChanged;
        viewModel.Alternatives.CollectionChanged += RoutesChanged;
        viewModel.RouteVertices.CollectionChanged += VerticesChanged;
        Disposable.Create(() =>
        {
            viewModel.Points.CollectionChanged -= PointsChanged;
            viewModel.Alternatives.CollectionChanged -= RoutesChanged;
            viewModel.RouteVertices.CollectionChanged -= VerticesChanged;
        }).DisposeWith(lifetime);
    }

    private static void BindMapState(
        MapViewModel viewModel,
        IMapAdapter adapter,
        CompositeDisposable lifetime)
    {
        viewModel.WhenAnyValue(x => x.SelectedRoute)
            .Subscribe(_ => adapter.SetRoutes(
                viewModel.Alternatives.ToArray(),
                viewModel.SelectedRoute?.Id))
            .DisposeWith(lifetime);

        viewModel.WhenAnyValue(x => x.SelectedRouteVertex)
            .Subscribe(_ => adapter.SetRouteVertices(viewModel.RouteVertices.ToArray()))
            .DisposeWith(lifetime);

        viewModel.WhenAnyValue(x => x.AddMode)
            .Subscribe(adapter.SetAddMode)
            .DisposeWith(lifetime);

        var readyAnnounced = false;
        viewModel.WhenAnyValue(x => x.MapVersion)
            .Subscribe(_ =>
            {
                adapter.SetFeatures(viewModel.Features.ToArray());
                if (readyAnnounced || viewModel.MapVersion <= 0 || viewModel.Features.Count == 0)
                {
                    return;
                }

                // Android routes stdout to logcat for the offline-map startup smoke test.
                Console.WriteLine(
                    $"CRP_OFFLINE_MAP_READY region={viewModel.DataInfo.RegionId} "
                    + $"features={viewModel.Features.Count}");
                readyAnnounced = true;
            })
            .DisposeWith(lifetime);
    }

    private void BindViewport(
        MapViewModel viewModel,
        IMapAdapter adapter,
        CompositeDisposable lifetime)
    {
        var changes = new System.Reactive.Subjects.Subject<(GeoBounds Bounds, double Resolution)>()
            .DisposeWith(lifetime);
        void ViewportChanged(GeoBounds bounds, double resolution) => changes.OnNext((bounds, resolution));

        adapter.ViewportChanged += ViewportChanged;
        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? latestMapLoad = null;
        var token = cancellation.Token;
        changes
            .Throttle(TimeSpan.FromMilliseconds(200), RxApp.MainThreadScheduler)
            .Subscribe(viewport =>
            {
                latestMapLoad?.Cancel();
                latestMapLoad = CancellationTokenSource.CreateLinkedTokenSource(token);
                _ = LoadViewportAsync(viewport.Bounds, viewport.Resolution, latestMapLoad);
            })
            .DisposeWith(lifetime);

        async Task LoadViewportAsync(GeoBounds bounds, double resolution, CancellationTokenSource request)
        {
            try
            {
                await viewModel.LoadMapAreaAsync(bounds, request.Token, resolution);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested)
            {
                System.Diagnostics.Debug.WriteLine("Устаревшая загрузка карты отменена.");
            }
            catch (Exception error)
            {
                viewModel.ReportUnexpectedError(error);
            }
            finally
            {
                if (ReferenceEquals(latestMapLoad, request))
                {
                    latestMapLoad = null;
                }

                request.Dispose();
            }
        }

        Disposable.Create(() =>
        {
            cancellation.Cancel();
            latestMapLoad?.Cancel();
            cancellation.Dispose();
            adapter.ViewportChanged -= ViewportChanged;
        }).DisposeWith(lifetime);
    }

    private void BindButtons()
    {
        ZoomInButton.Click += ZoomInClicked;
        ZoomOutButton.Click += ZoomOutClicked;
        NorthButton.Click += NorthClicked;
        SheetHandleButton.PointerPressed += SheetPointerPressed;
        SheetHandleButton.PointerMoved += SheetPointerMoved;
        SheetHandleButton.PointerReleased += SheetPointerReleased;
        SheetHandleButton.KeyDown += SheetHandleKeyDown;
    }

    private void UnbindButtons()
    {
        ZoomInButton.Click -= ZoomInClicked;
        ZoomOutButton.Click -= ZoomOutClicked;
        NorthButton.Click -= NorthClicked;
        SheetHandleButton.PointerPressed -= SheetPointerPressed;
        SheetHandleButton.PointerMoved -= SheetPointerMoved;
        SheetHandleButton.PointerReleased -= SheetPointerReleased;
        SheetHandleButton.KeyDown -= SheetHandleKeyDown;
        _activeAdapter = null;
        _panelPointer?.Capture(null);
        _panelPointer = null;
    }

    private void ZoomInClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        _activeAdapter?.ZoomIn();

    private void ZoomOutClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        _activeAdapter?.ZoomOut();

    private void NorthClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        _activeAdapter?.ResetNorth();

    private void SheetPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        _panelPointer = args.Pointer;
        _panelPointerStart = args.GetPosition(this);
        args.Pointer.Capture(SheetHandleButton);
        args.Handled = true;
    }

    private void SheetPointerMoved(object? sender, PointerEventArgs args)
    {
        if (_panelPointer?.Id != args.Pointer.Id)
        {
            return;
        }

        args.Handled = true;
    }

    private void SheetPointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (_panelPointer?.Id != args.Pointer.Id)
        {
            return;
        }

        var distance = args.GetPosition(this).Y - _panelPointerStart.Y;
        _panelPointer = null;
        args.Pointer.Capture(null);
        args.Handled = true;

        if (distance <= -PanelSwipeThreshold)
        {
            SetPanelExpanded(true);
        }
        else if (distance >= PanelSwipeThreshold)
        {
            SetPanelExpanded(false);
        }
        else
        {
            SetPanelExpanded(!_panelExpanded);
        }
    }

    private void SheetHandleKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key is not (Key.Enter or Key.Space))
        {
            return;
        }

        SetPanelExpanded(!_panelExpanded);
        args.Handled = true;
    }

    private void SetPanelExpanded(bool expanded)
    {
        _panelExpanded = expanded;
        AdaptLayout();
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs args) => AdaptLayout();

    private void AdaptLayout()
    {
        var layout = BottomSheetLayout.Create(Bounds.Width, Bounds.Height, _panelExpanded);
        var narrow = layout.IsNarrow;
        Layout.ColumnDefinitions = new(narrow ? "*" : "*,350");
        Layout.RowDefinitions = new("*");
        Grid.SetColumn(MapArea, 0);
        Grid.SetColumnSpan(MapArea, narrow ? 2 : 1);
        Grid.SetRow(SidePanel, 0);
        Grid.SetColumn(SidePanel, narrow ? 0 : 1);
        Grid.SetColumnSpan(SidePanel, narrow ? 2 : 1);
        SidePanel.VerticalAlignment = narrow
            ? Avalonia.Layout.VerticalAlignment.Bottom
            : Avalonia.Layout.VerticalAlignment.Stretch;
        SheetHandleButton.IsVisible = narrow;
        SidePanelContent.IsVisible = !narrow || _panelExpanded;
        SheetHandleText.Text = layout.HandleText;

        if (narrow)
        {
            SidePanel.Height = layout.Height!.Value;
            SidePanel.BorderThickness = new(1, 1, 1, 0);
            SidePanel.Padding = new(layout.Padding);
        }
        else
        {
            SidePanel.ClearValue(HeightProperty);
            SidePanel.BorderThickness = new(1, 0, 0, 0);
            SidePanel.Padding = new(18);
        }
    }
}
