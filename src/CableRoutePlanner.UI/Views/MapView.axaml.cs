using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.ReactiveUI;
using CableRoutePlanner.Domain;
using CableRoutePlanner.UI.Behaviors;
using CableRoutePlanner.UI.MapAdapters;
using CableRoutePlanner.ViewModels;
using ReactiveUI;

namespace CableRoutePlanner.UI.Views;

public partial class MapView : ReactiveUserControl<MapViewModel>
{
    private readonly BottomSheetController _sheet = new();
    private readonly Func<Mapsui.UI.Avalonia.MapControl, GeoBounds, IMapAdapter> _adapterFactory;

    public MapView(Func<Mapsui.UI.Avalonia.MapControl, GeoBounds, IMapAdapter> adapterFactory)
    {
        _adapterFactory = adapterFactory;
        InitializeComponent();
        this.WhenActivated(Activate);
        SizeChanged += OnSizeChanged;
    }

    private void Activate(CompositeDisposable lifetime)
    {
        MapViewModel viewModel = ViewModel!;
        // Detach subscriptions and cancel their tasks before disposing the adapter.
        CompositeDisposable bindings = new CompositeDisposable().DisposeWith(lifetime);
        IMapAdapter adapter = _adapterFactory(MapControl, viewModel.DataInfo.Bounds).DisposeWith(lifetime);
        adapter.ViewportStateChangedStream
            .Subscribe(state => viewModel.UpdateViewportState(state))
            .DisposeWith(bindings);
        Disposable.Create(() => viewModel.UpdateViewportState(adapter.CurrentViewportState))
            .DisposeWith(lifetime);
        MapGestureBindings.Bind(viewModel, adapter, bindings);
        MapLayerBindings.Bind(viewModel, adapter, bindings);
        _sheet.Bind(SheetHandleButton, this, AdaptLayout).DisposeWith(bindings);
        viewModel.BuildRoutes.ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (viewModel.HasRoute)
                {
                    ToolTabs.SelectedIndex = 1;
                }
                AdaptLayout();
            }).DisposeWith(bindings);
        viewModel.WhenAnyValue(vm => vm.HasRoute).Skip(1)
            .Subscribe(hasRoute =>
            {
                if (!hasRoute)
                {
                    ToolTabs.SelectedIndex = 0;
                }
                AdaptLayout();
            }).DisposeWith(bindings);
        viewModel.WhenAnyValue(vm => vm.PointCount)
            .Subscribe(_ => AdaptLayout()).DisposeWith(bindings);
        viewModel.Tracking?.WhenAnyValue(vm => vm.CompactProgressText)
            .Subscribe(_ => AdaptLayout()).DisposeWith(bindings);
        MapOverlay.ZoomInRequested += adapter.ZoomIn;
        MapOverlay.ZoomOutRequested += adapter.ZoomOut;
        MapOverlay.ResetNorthRequested += adapter.ResetNorth;
        MapOverlay.CenterLocationRequested += adapter.CenterOnLocation;
        Disposable.Create(() =>
        {
            MapOverlay.ZoomInRequested -= adapter.ZoomIn;
            MapOverlay.ZoomOutRequested -= adapter.ZoomOut;
            MapOverlay.ResetNorthRequested -= adapter.ResetNorth;
            MapOverlay.CenterLocationRequested -= adapter.CenterOnLocation;
        }).DisposeWith(bindings);
        adapter.NorthIndicatorRotationChangedStream
            .Subscribe(MapOverlay.SetNorthIndicatorRotation).DisposeWith(bindings);
        adapter.InitializeViewport(viewModel.ViewportState);
        adapter.SetPoints(viewModel.Points.ToArray());
        adapter.SetRoutes(viewModel.Alternatives.ToArray(), viewModel.SelectedRoute?.Id);
        adapter.SetRouteVertices(viewModel.RouteVertices.ToArray(), viewModel.ShowAllRouteVertices);
        AdaptLayout();

    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs args) => AdaptLayout();

    private void ShowTraversal(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        ToolTabs.SelectedIndex = 2;

    private void AdaptLayout()
    {
        MapViewModel? viewModel = ViewModel;
        BottomSheetLayout layout = BottomSheetLayout.Create(Bounds.Width, Bounds.Height, _sheet.IsExpanded);
        bool narrow = layout.IsNarrow;
        PointEditorScroll.VerticalScrollBarVisibility = narrow
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        PointEditorLayout.RowDefinitions = new(narrow ? "Auto,Auto" : "Auto,*");
        WaypointListScroll.VerticalScrollBarVisibility = narrow
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        PointEditingHint.Text = narrow
            ? "Добавьте точки на карте или введите координаты. Маркеры можно перетаскивать."
            : "Нажмите на карту, чтобы добавить точки. Маркеры можно перетаскивать; вращайте карту правой кнопкой мыши.";
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
        ToolTabs.IsVisible = !narrow || _sheet.IsExpanded;
        SheetHandleText.Text = narrow && !_sheet.IsExpanded
            ? viewModel?.Tracking is { HasRoute: true } tracking
                ? "↑ " + tracking.CompactProgressText
                : viewModel?.SelectedRoute is { } route
                    ? $"↑ Маршрут · {route.Metrics.LengthMetres:N0} м"
                    : $"↑ Точки: {viewModel?.PointCount ?? 0} · открыть панель"
            : layout.HandleText;

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
