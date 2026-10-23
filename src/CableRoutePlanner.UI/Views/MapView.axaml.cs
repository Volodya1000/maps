using System.Collections.Specialized;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.ReactiveUI;
using CableRoutePlanner.Domain;
using CableRoutePlanner.UI.MapAdapters;
using CableRoutePlanner.ViewModels;
using ReactiveUI;
namespace CableRoutePlanner.UI.Views;
public partial class MapView : ReactiveUserControl<MapViewModel>
{
    private readonly Func<Mapsui.UI.Avalonia.MapControl, GeoBounds, IMapAdapter> _adapterFactory;
    public MapView(Func<Mapsui.UI.Avalonia.MapControl, GeoBounds, IMapAdapter> adapterFactory)
    {
        _adapterFactory = adapterFactory; InitializeComponent();
        this.WhenActivated(disposables =>
        {
            var vm = ViewModel!;
            var adapter = _adapterFactory(MapControl, vm.DataInfo.Bounds).DisposeWith(disposables);
            void Points(object? s, NotifyCollectionChangedEventArgs e) => adapter.SetPoints(vm.Points.ToArray());
            void Routes(object? s, NotifyCollectionChangedEventArgs e) => adapter.SetRoutes(vm.Alternatives.ToArray(), vm.SelectedRoute?.Id);
            void Add(GeoCoordinate p) => vm.AddPoint.Execute(p).Subscribe().Dispose();
            void Move(Guid id, GeoCoordinate p) { vm.MovePoint(id, p); adapter.SetPoints(vm.Points.ToArray()); }
            adapter.PointAdded += Add; adapter.PointMoved += Move;
            vm.Points.CollectionChanged += Points; vm.Alternatives.CollectionChanged += Routes;
            // Features are rendered once after the asynchronous load, avoiding quadratic updates.
            vm.WhenAnyValue(x => x.SelectedRoute).Subscribe(_ => adapter.SetRoutes(vm.Alternatives.ToArray(), vm.SelectedRoute?.Id)).DisposeWith(disposables);
            vm.WhenAnyValue(x => x.AddMode).Subscribe(adapter.SetAddMode).DisposeWith(disposables);
            vm.WhenAnyValue(x => x.MapVersion).Subscribe(_ => adapter.SetFeatures(vm.Features.ToArray())).DisposeWith(disposables);
            var viewportChanges = new System.Reactive.Subjects.Subject<GeoBounds>().DisposeWith(disposables);
            void Viewport(GeoBounds bounds) => viewportChanges.OnNext(bounds);
            adapter.ViewportChanged += Viewport;
            var cancel = new CancellationTokenSource();
            viewportChanges.Throttle(TimeSpan.FromMilliseconds(200), RxApp.MainThreadScheduler)
                .Subscribe(bounds => { _ = vm.LoadMapAreaAsync(bounds, cancel.Token); }).DisposeWith(disposables);
            _ = Load();
            async Task Load() { await vm.LoadMapAsync(cancel.Token); }
            adapter.SetPoints(vm.Points.ToArray()); adapter.SetRoutes(vm.Alternatives.ToArray(), vm.SelectedRoute?.Id);
            void ZoomIn(object? s, Avalonia.Interactivity.RoutedEventArgs e) => adapter.ZoomIn();
            void ZoomOut(object? s, Avalonia.Interactivity.RoutedEventArgs e) => adapter.ZoomOut();
            ZoomInButton.Click += ZoomIn; ZoomOutButton.Click += ZoomOut;
            Disposable.Create(() =>
            {
                cancel.Cancel(); cancel.Dispose(); adapter.ViewportChanged -= Viewport; adapter.PointAdded -= Add; adapter.PointMoved -= Move;
                vm.Points.CollectionChanged -= Points; vm.Alternatives.CollectionChanged -= Routes;
                ZoomInButton.Click -= ZoomIn; ZoomOutButton.Click -= ZoomOut;
            }).DisposeWith(disposables);
        });
        SizeChanged += (_, _) => AdaptLayout();
    }
    private void AdaptLayout()
    {
        var narrow = Bounds.Width < 760;
        Layout.ColumnDefinitions = new(narrow ? "*" : "*,350");
        Layout.RowDefinitions = new(narrow ? "*,280,Auto" : "*,Auto");
        Grid.SetColumn(SidePanel, narrow ? 0 : 1); Grid.SetRow(SidePanel, narrow ? 1 : 0);
        Grid.SetRow(StatusPanel, narrow ? 2 : 1); Grid.SetColumnSpan(StatusPanel, narrow ? 1 : 2);
    }
}
