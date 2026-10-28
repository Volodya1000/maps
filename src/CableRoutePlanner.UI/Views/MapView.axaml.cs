using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using CableRoutePlanner.Domain;
using CableRoutePlanner.UI.Behaviors;
using CableRoutePlanner.UI.MapAdapters;
using CableRoutePlanner.ViewModels;
using ReactiveUI;

namespace CableRoutePlanner.UI.Views;

public partial class MapView : ReactiveUserControl<MapViewModel>
{
    private readonly RotateTransform _northArrowRotation = new();
    private readonly BottomSheetController _sheet = new();
    private readonly Func<Mapsui.UI.Avalonia.MapControl, GeoBounds, IMapAdapter> _adapterFactory;

    public MapView(Func<Mapsui.UI.Avalonia.MapControl, GeoBounds, IMapAdapter> adapterFactory)
    {
        _adapterFactory = adapterFactory;
        InitializeComponent();
        NorthArrow.RenderTransform = _northArrowRotation;
        this.WhenActivated(Activate);
        SizeChanged += OnSizeChanged;
    }

    private void Activate(CompositeDisposable lifetime)
    {
        MapViewModel viewModel = ViewModel!;
        // Detach subscriptions and cancel their tasks before disposing the adapter.
        CompositeDisposable bindings = new CompositeDisposable().DisposeWith(lifetime);
        IMapAdapter adapter = _adapterFactory(MapControl, viewModel.DataInfo.Bounds).DisposeWith(lifetime);
        MapGestureBindings.Bind(viewModel, adapter, bindings);
        MapLayerBindings.Bind(viewModel, adapter, bindings);
        _sheet.Bind(SheetHandleButton, this, AdaptLayout).DisposeWith(bindings);
        Observable.FromEvent<Action<double>, double>(handler => angle => handler(angle),
                handler => adapter.NorthIndicatorRotationChanged += handler,
                handler => adapter.NorthIndicatorRotationChanged -= handler)
            .Subscribe(angle => _northArrowRotation.Angle = angle).DisposeWith(bindings);
        BindButton(ZoomInButton, adapter.ZoomIn);
        BindButton(ZoomOutButton, adapter.ZoomOut);
        BindButton(NorthButton, adapter.ResetNorth);
        adapter.InitializeViewport();
        adapter.SetPoints(viewModel.Points.ToArray());
        adapter.SetRoutes(viewModel.Alternatives.ToArray(), viewModel.SelectedRoute?.Id);
        adapter.SetRouteVertices(viewModel.RouteVertices.ToArray());
        AdaptLayout();

        void BindButton(Button button, Action action) =>
            Observable.FromEventPattern<RoutedEventArgs>(handler => button.Click += handler,
                    handler => button.Click -= handler)
                .Subscribe(_ => action()).DisposeWith(bindings);
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs args) => AdaptLayout();

    private void AdaptLayout()
    {
        BottomSheetLayout layout = BottomSheetLayout.Create(Bounds.Width, Bounds.Height, _sheet.IsExpanded);
        bool narrow = layout.IsNarrow;
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
        SidePanelContent.IsVisible = !narrow || _sheet.IsExpanded;
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
