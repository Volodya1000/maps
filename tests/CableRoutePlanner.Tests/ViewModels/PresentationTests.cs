using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Diagnostics;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.ViewModels;
using ReactiveUI;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class PresentationTests : ReactiveTestScope
{
    private static readonly IReadOnlyList<int> ThreePointNumbers = Array.AsReadOnly(new[] { 1, 2, 3 });
    private static readonly IReadOnlyList<int> TwoPointNumbers = Array.AsReadOnly(new[] { 1, 2 });
    [Fact]
    public void FodyWeavingRaisesPropertyChangedAfterRealCompilation()
    {
        using var vm = new SettingsViewModel(new NavigationHost(), new MemorySettings());
        var names = new List<string?>();
        vm.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        vm.CellSize = 37;
        vm.ReservePercent = 8;
        Assert.Contains(nameof(vm.CellSize), names);
        Assert.Contains(nameof(vm.ReservePercent), names);
    }

    [Fact]
    public async Task PointChangesInvalidateRoutesAndRenumberList()
    {
        var data = new MemoryData();
        using var vm = CreateVm(TestData.Planner(), data);
        await vm.AddPoint.Execute(TestData.P(50, 150));
        await vm.AddPoint.Execute(TestData.P(260, 150));
        await vm.BuildRoutes.Execute();
        Assert.NotEmpty(vm.Alternatives);
        await vm.MovePointAsync(vm.Points[0].Id, TestData.P(55, 155));
        Assert.Empty(vm.Alternatives);
        Assert.Null(vm.SelectedRoute);
        vm.SelectedPoint = vm.Points[0];
        await vm.DeletePoint.Execute();
        Assert.Equal(1, vm.Points[0].Number);
        Assert.False(vm.CanBuild);
    }

    [Fact]
    public async Task ClipboardUsesSelectedPointAndOrderIsControlledByList()
    {
        var clipboard = new MemoryClipboard();
        var session = new PlanningSession(TestData.Planner());
        using var vm = new MapViewModel(
            new NavigationHost(),
            session,
            new MemoryData(),
            new MemorySettings(),
            clipboard);
        await vm.AddPoint.Execute(TestData.P(50, 150));
        await vm.AddPoint.Execute(TestData.P(260, 150));
        var last = vm.Points[1];
        vm.SelectedPoint = last;
        await vm.MoveUp.Execute();
        Assert.Equal(last.Id, vm.Points[0].Id);
        await vm.CopyCoordinates.Execute();
        Assert.Equal(last.Coordinate.ToString(), clipboard.Text);
    }

    [Fact]
    public async Task ChangedInputCannotPublishLateCalculation()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<RouteAlternative>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new PlanningSession(new DeferredPlanner(pending.Task));
        session.Add(TestData.P(50, 150));
        session.Add(TestData.P(260, 150));
        var build = session.BuildAsync(TestData.Options, default);
        session.Move(session.Points[0].Id, TestData.P(55, 155));
        pending.SetResult([]);
        await Assert.ThrowsAsync<PlanningException>(() => build);
    }

    [Fact]
    public async Task CancelledOldBuildCannotPublishAfterANewerBuildStarts()
    {
        var planner = new SequentialDeferredPlanner();
        using var vm = CreateVm(planner, new MemoryData());
        await vm.AddPoint.Execute(TestData.P(50, 150));
        await vm.AddPoint.Execute(TestData.P(260, 150));

        var oldBuild = vm.BuildRoutes.Execute().ToTask();
        var oldRequest = await planner.WaitForRequestAsync(0);
        await vm.MovePointAsync(vm.Points[0].Id, TestData.P(55, 155));
        oldRequest.SetResult([Route("устаревший", TestData.P(50, 150), TestData.P(260, 150))]);
        await oldBuild;

        Assert.Empty(vm.Alternatives);
        Assert.False(vm.IsBusy);

        var currentBuild = vm.BuildRoutes.Execute().ToTask();
        var currentRequest = await planner.WaitForRequestAsync(1);
        var currentGeometry = new[] { vm.Points[0].Coordinate, vm.Points[1].Coordinate };
        currentRequest.SetResult([Route("актуальный", currentGeometry[0], currentGeometry[1])]);
        await currentBuild;

        Assert.Equal("актуальный", Assert.Single(vm.Alternatives).Name);
        Assert.False(vm.IsBusy);
        Assert.Contains("Найдено", vm.Status);
    }

    [Fact]
    public async Task NavigationReusesScreenInstancesAndBoundsHistory()
    {
        var screen = new NavigationHost();
        var data = new MemoryData();
        using var map = CreateVm(TestData.Planner(), data, screen);
        using var settings = new SettingsViewModel(screen, new MemorySettings());
        using var shell = new ShellViewModel(screen, map, settings);
        for (var i = 0; i < 100; i++)
        {
            await shell.ShowSettings.Execute();
            await shell.ShowMap.Execute();
        }

        Assert.InRange(screen.Router.NavigationStack.Count, 1, 3);
        Assert.Same(map, screen.Router.GetCurrentViewModel());
        Assert.True(shell.IsMap);
        await shell.Back.Execute();
        Assert.Same(settings, screen.Router.GetCurrentViewModel());
    }

    [Fact]
    public async Task SingleResultIsPresentedAsARouteNotAsAProfileAlternative()
    {
        using var vm = CreateVm(TestData.Planner(), new MemoryData());
        await vm.AddPoint.Execute(TestData.P(50, 150));
        await vm.AddPoint.Execute(TestData.P(260, 150));
        await vm.BuildRoutes.Execute();

        Assert.Single(vm.Alternatives);
        Assert.False(vm.HasAlternatives);
        Assert.Equal("Маршрут", Assert.Single(vm.RouteChoices).Name);
    }

    [Fact]
    public async Task ReorderingRebuildsRouteAndDeletingByIdPreservesSlots()
    {
        using var vm = CreateVm(TestData.Planner(), new MemoryData());
        await vm.AddPoint.Execute(TestData.P(40, 150));
        await vm.AddPoint.Execute(TestData.P(200, 150));
        await vm.AddPoint.Execute(TestData.P(300, 150));
        var original = vm.Points.Select(point => point.Id).ToArray();
        await vm.BuildRoutes.Execute();
        Assert.NotEmpty(vm.RouteChoices);

        await vm.ReorderPoints.Execute(new WaypointReorder(original[0], 2));
        Assert.Equal(new[] { original[1], original[2], original[0] }, vm.Points.Select(point => point.Id));
        Assert.Equal(ThreePointNumbers, vm.Points.Select(point => point.Number));
        Assert.NotEmpty(vm.Alternatives);
        Assert.NotEmpty(vm.RouteChoices);
        Assert.NotNull(vm.SelectedRoute);
        Assert.True(vm.HasRoute);
        Assert.True(vm.SelectedRoute!.Geometry[0].DistanceTo(vm.Points[0].Coordinate) < 0.0001);
        Assert.True(vm.SelectedRoute.Geometry[^1].DistanceTo(vm.Points[^1].Coordinate) < 0.0001);

        vm.SelectedPoint = vm.Points[0];
        await vm.RemovePoint.Execute(original[2]);
        Assert.Equal(new[] { original[1], original[0] }, vm.Points.Select(point => point.Id));
        Assert.Equal(TwoPointNumbers, vm.Points.Select(point => point.Number));
        Assert.Equal(original[1], vm.SelectedPoint?.Id);
    }

    [Fact]
    public async Task ChoosingAlternativeChangesGeometryMetricsAndSelectedChoice()
    {
        var forest = TestData.Polygon(1, TerrainKind.Forest, 100, 60, 220, 240);
        using var vm = CreateVm(TestData.Planner(forest), new MemoryData(forest));
        await vm.AddPoint.Execute(TestData.P(30, 150));
        await vm.AddPoint.Execute(TestData.P(300, 150));
        await vm.BuildRoutes.Execute();
        Assert.True(vm.Alternatives.Count > 1);
        Assert.True(vm.HasAlternatives);
        var first = vm.SelectedRoute!;
        var other = vm.Alternatives.First(route => route.Id != first.Id);
        await vm.ChooseRoute.Execute(other.Id);
        Assert.Same(other, vm.SelectedRoute);
        Assert.NotEqual(first.Geometry, vm.SelectedRoute!.Geometry);
        Assert.Equal(other.Id, Assert.Single(vm.RouteChoices, choice => choice.IsSelected).Id);
        Assert.Contains($"Кабель с запасом {0.10:P0}: {other.Metrics.LengthMetres * 1.10:N0} м", vm.MetricsText);
    }

    [Fact]
    public async Task ReorderingDuringCalculationCancelsAndBuildsLatestOrder()
    {
        var planner = new CancelFirstPlanner();
        using var vm = CreateVm(planner, new MemoryData());
        await vm.AddPoint.Execute(TestData.P(30, 150));
        await vm.AddPoint.Execute(TestData.P(200, 150));
        await vm.AddPoint.Execute(TestData.P(300, 150));
        var first = vm.Points[0].Id;
        var build = vm.BuildRoutes.Execute().ToTask();
        await planner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.ReorderPoints.Execute(new WaypointReorder(first, 2)).ToTask().WaitAsync(TimeSpan.FromSeconds(5));
        await build.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(first, vm.Points[2].Id);
        Assert.True(vm.HasRoute);
        Assert.True(vm.SelectedRoute!.Geometry[0].DistanceTo(vm.Points[0].Coordinate) < 0.0001);
        Assert.True(vm.SelectedRoute.Geometry[^1].DistanceTo(vm.Points[^1].Coordinate) < 0.0001);
    }

    private sealed class CancelFirstPlanner : IRoutePlanner
    {
        public TaskCompletionSource Started { get; } = new();
        private int _calls;
        public async Task<IReadOnlyList<RouteAlternative>> PlanAsync(
            IReadOnlyList<GeoCoordinate> points, RoutingOptions options, CancellationToken ct)
        {
            if (++_calls == 1)
            {
                Started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return await TestData.Planner().PlanAsync(points, options, ct);
        }
    }

    private static MapViewModel CreateVm(
        IRoutePlanner planner,
        IGeodataRepository data,
        IScreen? screen = null) => new(
        screen ?? new NavigationHost(),
        new PlanningSession(planner),
        data,
        new MemorySettings(),
        new MemoryClipboard());

    private sealed class DeferredPlanner(Task<IReadOnlyList<RouteAlternative>> result) : IRoutePlanner
    {
        public Task<IReadOnlyList<RouteAlternative>> PlanAsync(
            IReadOnlyList<GeoCoordinate> p,
            RoutingOptions o,
            CancellationToken ct) => result;
    }

    private static RouteAlternative Route(string name, GeoCoordinate start, GeoCoordinate end) => new(
        Guid.NewGuid(), name, [start, end], new RouteMetrics(100, 0, 0, 0, 0, 0, 100, null));

    private sealed class SequentialDeferredPlanner : IRoutePlanner
    {
        private readonly List<TaskCompletionSource<IReadOnlyList<RouteAlternative>>> _requests = [];

        public Task<IReadOnlyList<RouteAlternative>> PlanAsync(
            IReadOnlyList<GeoCoordinate> points,
            RoutingOptions options,
            CancellationToken cancellationToken)
        {
            var request = new TaskCompletionSource<IReadOnlyList<RouteAlternative>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_requests)
            {
                _requests.Add(request);
            }

            return request.Task;
        }

        public async Task<TaskCompletionSource<IReadOnlyList<RouteAlternative>>> WaitForRequestAsync(int index)
        {
            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromSeconds(2))
            {
                lock (_requests)
                {
                    if (_requests.Count > index)
                    {
                        return _requests[index];
                    }
                }

                await Task.Delay(5);
            }

            throw new TimeoutException($"Planner request {index} did not start.");
        }
    }
}
