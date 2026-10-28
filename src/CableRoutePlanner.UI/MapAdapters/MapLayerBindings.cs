using System.Collections.Specialized;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CableRoutePlanner.Domain;
using CableRoutePlanner.ViewModels;
using ReactiveUI;

namespace CableRoutePlanner.UI.MapAdapters;

internal static class MapLayerBindings
{
    public static void Bind(MapViewModel viewModel, IMapAdapter adapter, CompositeDisposable lifetime)
    {
        CollectionChanges(viewModel.Points)
            .Subscribe(_ => adapter.SetPoints(viewModel.Points.ToArray())).DisposeWith(lifetime);
        CollectionChanges(viewModel.Alternatives)
            .Subscribe(_ => adapter.SetRoutes(viewModel.Alternatives.ToArray(), viewModel.SelectedRoute?.Id)).DisposeWith(lifetime);
        CollectionChanges(viewModel.RouteVertices)
            .Subscribe(_ => adapter.SetRouteVertices(viewModel.RouteVertices.ToArray(), viewModel.ShowAllRouteVertices)).DisposeWith(lifetime);
        viewModel.WhenAnyValue(vm => vm.SelectedRoute)
            .Subscribe(_ => adapter.SetRoutes(viewModel.Alternatives.ToArray(), viewModel.SelectedRoute?.Id)).DisposeWith(lifetime);
        viewModel.WhenAnyValue(vm => vm.SelectedRoute, vm => vm.ShowRoadContacts)
            .Subscribe(state => adapter.SetRoadContacts(state.Item2 ? state.Item1?.RoadContacts ?? [] : []))
            .DisposeWith(lifetime);
        viewModel.WhenAnyValue(vm => vm.SelectedRouteVertex, vm => vm.ShowAllRouteVertices)
            .Subscribe(_ => adapter.SetRouteVertices(viewModel.RouteVertices.ToArray(), viewModel.ShowAllRouteVertices)).DisposeWith(lifetime);
        viewModel.WhenAnyValue(vm => vm.AddMode).Subscribe(adapter.SetAddMode).DisposeWith(lifetime);
        viewModel.WhenAnyValue(vm => vm.MapVersion)
            .Select(_ => Observable.FromAsync(() => adapter.SetFeaturesAsync(viewModel.Features))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Catch<Unit, Exception>(error => Report(viewModel, error)))
            .Switch().Subscribe().DisposeWith(lifetime);
        Observable.FromEvent<Action<GeoBounds, double>, (GeoBounds Bounds, double Resolution)>(
                handler => (bounds, resolution) => handler((bounds, resolution)),
                handler => adapter.ViewportChanged += handler, handler => adapter.ViewportChanged -= handler)
            .Throttle(TimeSpan.FromMilliseconds(200), RxApp.MainThreadScheduler)
            .Select(viewport => Observable.FromAsync(token =>
                    viewModel.LoadMapAreaAsync(viewport.Bounds, token, viewport.Resolution))
                .Catch<Unit, OperationCanceledException>(_ => Observable.Empty<Unit>())
                .ObserveOn(RxApp.MainThreadScheduler)
                .Catch<Unit, Exception>(error => Report(viewModel, error)))
            .Switch().Subscribe().DisposeWith(lifetime);
    }

    private static IObservable<System.Reactive.EventPattern<NotifyCollectionChangedEventArgs>> CollectionChanges(INotifyCollectionChanged collection) =>
        Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
            handler => collection.CollectionChanged += handler, handler => collection.CollectionChanged -= handler);

    private static IObservable<Unit> Report(MapViewModel viewModel, Exception error)
    {
        viewModel.ReportUnexpectedError(error);
        return Observable.Empty<Unit>();
    }
}
