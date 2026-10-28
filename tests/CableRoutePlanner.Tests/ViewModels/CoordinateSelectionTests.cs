using System.Reactive.Linq;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.ViewModels;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class CoordinateSelectionTests : ReactiveTestScope
{
    private static readonly int[] PrimaryPointNumbers = [1, 2, 3];

    private static readonly string[] BridgeDisplayLabels =
        ["1", "1.1", "1.2", "1.3", "2", "2.1", "2.2", "3"];

    private static readonly GeoCoordinate[] BridgeDisplayCoordinates =
    [
        TestData.P(70, 0), TestData.P(85, 0), TestData.P(120, 20),
        TestData.P(225, 0), TestData.P(237, 0)
    ];

    [Fact]
    public async Task SelectingCopyingAndRemovingWaypointKeepVisibleSelectionConsistent()
    {
        var clipboard = new MemoryClipboard();
        using MapViewModel vm = TestData.CreateMapViewModel(new NavigationHost(), new PlanningSession(TestData.Planner(), TestData.AllowAllValidator),
            new MapAreaLoader(new MemoryData()), new MemorySettings(), clipboard);
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
        using MapViewModel vm = TestData.CreateMapViewModel(new NavigationHost(), new PlanningSession(new VertexPlanner(), TestData.AllowAllValidator),
            new MapAreaLoader(data), new MemorySettings(), clipboard);
        await vm.AddPoint.Execute(TestData.P(50, 150));
        await vm.AddPoint.Execute(TestData.P(260, 150));
        await vm.BuildRoutes.Execute();
        Assert.Equal(vm.SelectedRoute!.Geometry.Distinct(), vm.RouteVertices.Select(row => row.Coordinate));
        Assert.Equal(Enumerable.Range(1, 3), vm.RouteVertices.Select(row => row.Number));
        // A point can be both a bridge transition and a significant change of direction.
        // Map rendering must receive Turn rather than hide it as an auxiliary Transition.
        Assert.Equal(RouteVertexKind.Turn, vm.RouteVertices[1].Kind);
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

    [Fact]
    public async Task StraightBridgeTransitionKeepsAuxiliaryKindWithoutInventingATurn()
    {
        using MapViewModel vm = TestData.CreateMapViewModel(new NavigationHost(),
            new PlanningSession(new StraightTransitionPlanner(), TestData.AllowAllValidator),
            new MapAreaLoader(new MemoryData()), new MemorySettings(), new MemoryClipboard());
        await vm.AddPoint.Execute(TestData.P(50, 150));
        await vm.AddPoint.Execute(TestData.P(260, 150));
        await vm.BuildRoutes.Execute();

        Assert.Equal(3, vm.RouteVertices.Count);
        Assert.Equal(RouteVertexKind.Transition, vm.RouteVertices[1].Kind);
        Assert.Equal(TestData.P(155, 150), vm.RouteVertices[1].Coordinate);
    }

    [Fact]
    public async Task ShortZigzagBendsAreAllNumberedEvenWhenAdjacentSegmentsAreBelowFifteenMetres()
    {
        using MapViewModel vm = TestData.CreateMapViewModel(new NavigationHost(),
            new PlanningSession(new ShortZigzagPlanner(), TestData.AllowAllValidator),
            new MapAreaLoader(new MemoryData()), new MemorySettings(), new MemoryClipboard());
        await vm.AddPoint.Execute(TestData.P(50, 150));
        await vm.AddPoint.Execute(TestData.P(120, 158));
        await vm.BuildRoutes.Execute();

        // Four real bends on 7-10 m segments, plus the user-controlled endpoints.
        Assert.Equal(6, vm.RouteVertices.Count);
        Assert.Equal(Enumerable.Range(1, 6), vm.RouteVertices.Select(vertex => vertex.Number));
        Assert.Equal(
            new[] { TestData.P(58, 150), TestData.P(64, 154), TestData.P(72, 154), TestData.P(78, 158) },
            vm.RouteVertices.Where(vertex => vertex.Kind == RouteVertexKind.Turn)
                .Select(vertex => vertex.Coordinate));
        Assert.Equal(2, vm.RouteVertices.Count(vertex => vertex.Kind == RouteVertexKind.UserWaypoint));
    }

    [Fact]
    public async Task MultipleLegsKeepHumanLabelsAndCollapseDuplicateBridgeMarkers()
    {
        var clipboard = new MemoryClipboard();
        using MapViewModel vm = TestData.CreateMapViewModel(new NavigationHost(),
            new PlanningSession(new ClusteredBridgePlanner(), TestData.AllowAllValidator),
            new MapAreaLoader(new MemoryData()), new MemorySettings(), clipboard);
        await vm.AddPoint.Execute(TestData.P(0, 0));
        await vm.AddPoint.Execute(TestData.P(160, 0));
        await vm.AddPoint.Execute(TestData.P(320, 0));
        await vm.BuildRoutes.Execute();

        Assert.Equal(
            BridgeDisplayLabels,
            vm.RouteVertices.Select(vertex => vertex.DisplayLabel));
        Assert.Equal(
            BridgeDisplayCoordinates,
            vm.RouteVertices.Where(vertex => vertex.Kind != RouteVertexKind.UserWaypoint)
                .Select(vertex => vertex.Coordinate));
        Assert.Equal(Enumerable.Range(1, 8), vm.RouteVertices.Select(vertex => vertex.Number));
        Assert.Equal(PrimaryPointNumbers,
            vm.RouteVertices.Where(vertex => vertex.Kind == RouteVertexKind.UserWaypoint)
                .Select(vertex => vertex.LegIndex));

        vm.SelectedRouteVertex = vm.RouteVertices[5];
        Assert.Equal("Копировать вершину 2.1", vm.CopyRouteVertexText);
        await vm.CopyRouteVertex.Execute();
        Assert.Contains("2.1", vm.Status);
        Assert.Equal(vm.RouteVertices[5].Coordinate.ToString(), clipboard.Text);
    }

    [Fact]
    public async Task RouteLegsStartCollapsedAndExpandIndependentlyWithoutChangingMapVertices()
    {
        using MapViewModel vm = TestData.CreateMapViewModel(new NavigationHost(),
            new PlanningSession(new ClusteredBridgePlanner(), TestData.AllowAllValidator),
            new MapAreaLoader(new MemoryData()), new MemorySettings(), new MemoryClipboard());
        await vm.AddPoint.Execute(TestData.P(0, 0));
        await vm.AddPoint.Execute(TestData.P(160, 0));
        await vm.AddPoint.Execute(TestData.P(320, 0));
        await vm.BuildRoutes.Execute();
        RouteVertexRow[] fullVertices = vm.RouteVertices.ToArray();
        Assert.Equal(3, vm.VisibleRouteVertices.Count);
        Assert.All(vm.VisibleRouteVertices, vertex => Assert.False(vertex.IsChild));
        Assert.True(vm.VisibleRouteVertices[1].HasChildren);
        await vm.ToggleRouteLeg.Execute(2);
        Assert.Contains(vm.VisibleRouteVertices, vertex => vertex.DisplayLabel == "2.1");
        Assert.DoesNotContain(vm.VisibleRouteVertices, vertex => vertex.DisplayLabel == "1.1");
        await vm.ToggleRouteLeg.Execute(2);
        Assert.Equal(3, vm.VisibleRouteVertices.Count);
        Assert.Equal(fullVertices, vm.RouteVertices);
        RouteVertexRow child = vm.RouteVertices.First(vertex => vertex.DisplayLabel == "2.1");
        await vm.SelectRouteVertex.Execute(child.Number);
        Assert.Contains(vm.VisibleRouteVertices, vertex => vertex.Number == child.Number && vertex.IsSelected);
        Assert.DoesNotContain(vm.VisibleRouteVertices, vertex => vertex.DisplayLabel == "1.1");
        RouteVertexRow primary = vm.RouteVertices.First(vertex => vertex.Kind == RouteVertexKind.UserWaypoint);
        await vm.SelectRouteVertex.Execute(primary.Number);
        Assert.Equal(primary.Number, Assert.Single(vm.RouteVertices, vertex => vertex.IsSelected).Number);
        Assert.Contains(vm.VisibleRouteVertices, vertex => vertex.Number == primary.Number && vertex.IsSelected);
        RouteChoice choice = Assert.Single(vm.RouteChoices);
        Assert.Equal($"Лес {vm.SelectedRoute!.Metrics.ForestMetres:N0} м", choice.ForestText);
        Assert.Equal($"Поле {vm.SelectedRoute.Metrics.FieldMetres:N0} м", choice.FieldText);
        Assert.Equal($"Мосты {vm.SelectedRoute.Metrics.PotentialBridgeCrossings}", choice.BridgesText);
        await vm.ClearPoints.Execute();
        Assert.Empty(vm.VisibleRouteVertices);
    }

    private sealed class ClusteredBridgePlanner : IRoutePlanner
    {
        public Task<IReadOnlyList<RouteAlternative>> PlanAsync(
            IReadOnlyList<GeoCoordinate> points, RoutingOptions options, CancellationToken cancellationToken)
        {
            var metrics = new RouteMetrics(340, 0, 340, 0, 0, 0, 340, null);
            return Task.FromResult<IReadOnlyList<RouteAlternative>>(
            [
                new(Guid.NewGuid(), "Мосты с дублирующимися точками",
                [
                    points[0],
                    TestData.P(70, 0),
                    TestData.P(71, 0),
                    TestData.P(72, 1),
                    TestData.P(73, 0),
                    TestData.P(85, 0),
                    TestData.P(120, 20),
                    points[1],
                    TestData.P(225, 0),
                    TestData.P(226, 0),
                    TestData.P(237, 0),
                    points[2]
                ],
                metrics,
                [
                    TestData.P(70, 0), TestData.P(71, 0), TestData.P(73, 0),
                    TestData.P(85, 0), TestData.P(225, 0),
                    TestData.P(226, 0), TestData.P(237, 0)
                ])
            ]);
        }
    }

    private sealed class ShortZigzagPlanner : IRoutePlanner
    {
        public Task<IReadOnlyList<RouteAlternative>> PlanAsync(
            IReadOnlyList<GeoCoordinate> points, RoutingOptions options, CancellationToken cancellationToken)
        {
            var metrics = new RouteMetrics(100, 0, 100, 0, 0, 0, 100, null);
            return Task.FromResult<IReadOnlyList<RouteAlternative>>(
            [
                new(Guid.NewGuid(), "Короткие повороты",
                [
                    points[0],
                    TestData.P(58, 150),
                    TestData.P(64, 154),
                    TestData.P(72, 154),
                    TestData.P(78, 158),
                    TestData.P(90, 158), // Collinear: must not become a false marker.
                    points[1]
                ], metrics)
            ]);
        }
    }

    private sealed class StraightTransitionPlanner : IRoutePlanner
    {
        public Task<IReadOnlyList<RouteAlternative>> PlanAsync(
            IReadOnlyList<GeoCoordinate> points, RoutingOptions options, CancellationToken cancellationToken)
        {
            GeoCoordinate transition = TestData.P(155, 150);
            var metrics = new RouteMetrics(210, 0, 210, 0, 0, 0, 210, null);
            return Task.FromResult<IReadOnlyList<RouteAlternative>>(
            [
                new(Guid.NewGuid(), "Прямой переход",
                    [points[0], transition, points[1]], metrics, [transition])
            ]);
        }
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
