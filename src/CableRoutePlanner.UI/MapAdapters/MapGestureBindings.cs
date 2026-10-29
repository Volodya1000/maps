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
        adapter.PointAddedStream
            .SelectMany(point => viewModel.AddPoint.Execute(point))
            .Subscribe(_ => { }, viewModel.ReportUnexpectedError).DisposeWith(lifetime);

        adapter.WaypointSelectedStream
            .SelectMany(id => viewModel.SelectPoint.Execute(id))
            .Subscribe(_ => { }, viewModel.ReportUnexpectedError).DisposeWith(lifetime);

        adapter.RouteVertexSelectedStream
            .Subscribe(number => viewModel.SelectedRouteVertex =
                viewModel.RouteVertices.FirstOrDefault(vertex => vertex.Number == number))
            .DisposeWith(lifetime);

        adapter.PointMovedStream
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
