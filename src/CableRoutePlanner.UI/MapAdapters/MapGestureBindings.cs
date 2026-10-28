using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CableRoutePlanner.Domain;
using CableRoutePlanner.ViewModels;
using ReactiveUI;

namespace CableRoutePlanner.UI.MapAdapters;

internal static class MapGestureBindings
{
    public static void Bind(MapViewModel viewModel, IMapAdapter adapter, CompositeDisposable lifetime)
    {
        Observable.FromEvent<Action<GeoCoordinate>, GeoCoordinate>(handler => point => handler(point),
                handler => adapter.PointAdded += handler, handler => adapter.PointAdded -= handler)
            .SelectMany(point => viewModel.AddPoint.Execute(point))
            .Subscribe(_ => { }, viewModel.ReportUnexpectedError).DisposeWith(lifetime);

        Observable.FromEvent<Action<Guid>, Guid>(handler => id => handler(id),
                handler => adapter.WaypointSelected += handler, handler => adapter.WaypointSelected -= handler)
            .SelectMany(id => viewModel.SelectPoint.Execute(id))
            .Subscribe(_ => { }, viewModel.ReportUnexpectedError).DisposeWith(lifetime);

        Observable.FromEvent<Action<int>, int>(handler => number => handler(number),
                handler => adapter.RouteVertexSelected += handler, handler => adapter.RouteVertexSelected -= handler)
            .Subscribe(number => viewModel.SelectedRouteVertex =
                viewModel.RouteVertices.FirstOrDefault(vertex => vertex.Number == number))
            .DisposeWith(lifetime);

        Observable.FromEvent<Action<Guid, GeoCoordinate>, (Guid Id, GeoCoordinate Coordinate)>(
                handler => (id, point) => handler((id, point)),
                handler => adapter.PointMoved += handler, handler => adapter.PointMoved -= handler)
            .Select(move => Observable.FromAsync(() => viewModel.MovePointAsync(move.Id, move.Coordinate))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Do(_ => adapter.SetPoints(viewModel.Points.ToArray()))
                .Catch<Unit, Exception>(error =>
                {
                    viewModel.ReportUnexpectedError(error);
                    return Observable.Empty<Unit>();
                }))
            .Switch().Subscribe().DisposeWith(lifetime);
    }
}
