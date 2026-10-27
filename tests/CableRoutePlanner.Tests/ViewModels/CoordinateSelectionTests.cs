using System.Reactive.Linq;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.ViewModels;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class CoordinateSelectionTests : ReactiveTestScope
{
    [Fact]
    public async Task SelectingCopyingAndRemovingWaypointKeepVisibleSelectionConsistent()
    {
        var clipboard = new MemoryClipboard();
        using var vm = new MapViewModel(new NavigationHost(), new PlanningSession(TestData.Planner(), TestData.AllowAllValidator),
            new MemoryData(), new MemorySettings(), clipboard);
        await vm.AddPoint.Execute(TestData.P(50, 150));
        await vm.AddPoint.Execute(TestData.P(260, 150));
        Assert.Equal(vm.SelectedPoint?.Id, Assert.Single(vm.Points, row => row.IsSelected).Id);
        WaypointRow first = vm.Points[0];
        await vm.SelectPoint.Execute(first.Id);
        Assert.Equal(first.Id, Assert.Single(vm.Points, row => row.IsSelected).Id);
        Assert.Contains("1", vm.CopyPointText);
        await vm.CopyCoordinates.Execute();
        Assert.Equal(first.Display, clipboard.Text);
        await vm.RemovePoint.Execute(first.Id);
        Assert.Equal(vm.SelectedPoint?.Id, Assert.Single(vm.Points, row => row.IsSelected).Id);
        await vm.ClearPoints.Execute();
        Assert.Null(vm.SelectedPoint);
    }

    [Fact]
    public async Task RouteVerticesIncludeTurnsAndUpdateWhenChoosingAnAlternativeOrEditingInput()
    {
        var clipboard = new MemoryClipboard();
        var data = new MemoryData();
        using var vm = new MapViewModel(new NavigationHost(), new PlanningSession(new VertexPlanner(), TestData.AllowAllValidator),
            data, new MemorySettings(), clipboard);
        await vm.AddPoint.Execute(TestData.P(50, 150));
        await vm.AddPoint.Execute(TestData.P(260, 150));
        await vm.BuildRoutes.Execute();
        Assert.Equal(vm.SelectedRoute!.Geometry.Distinct(), vm.RouteVertices.Select(row => row.Coordinate));
        Assert.Equal(Enumerable.Range(1, 3), vm.RouteVertices.Select(row => row.Number));
        Assert.Equal(RouteVertexKind.Transition, vm.RouteVertices[1].Kind);
        vm.SelectedRouteVertex = vm.RouteVertices[1];
        await vm.CopyRouteVertex.Execute();
        Assert.Equal(vm.RouteVertices[1].Display, clipboard.Text);
        await vm.ChooseRoute.Execute(vm.Alternatives[1].Id);
        Assert.Null(vm.SelectedRouteVertex);
        Assert.Equal(vm.SelectedRoute!.Geometry, vm.RouteVertices.Select(row => row.Coordinate));
        await vm.MovePointAsync(vm.Points[0].Id, TestData.P(70, 150));
        Assert.Empty(vm.RouteVertices);
        Assert.Null(vm.SelectedRouteVertex);
    }

    private sealed class VertexPlanner : IRoutePlanner
    {
        public Task<IReadOnlyList<RouteAlternative>> PlanAsync(IReadOnlyList<GeoCoordinate> points,
            RoutingOptions options, CancellationToken cancellationToken)
        {
            GeoCoordinate turn = TestData.P(120, 200);
            var metrics = new RouteMetrics(100, 40, 60, 1, 2, 0, 110, null);
            return Task.FromResult<IReadOnlyList<RouteAlternative>>([
                new(Guid.NewGuid(), "Первый", [points[0], turn, turn, points[1]], metrics, [turn]),
                new(Guid.NewGuid(), "Второй", [points[0], TestData.P(150, 100), points[1]], metrics)
            ]);
        }
    }
}
