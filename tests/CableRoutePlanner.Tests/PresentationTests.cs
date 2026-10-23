using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.ViewModels;
using ReactiveUI;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class PresentationTests : ReactiveTestScope
{
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
        vm.MovePoint(vm.Points[0].Id, TestData.P(55, 155));
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
    public async Task ReorderingRebuildsRouteAndDeletingByIdPreservesSlots()
    {
        using var vm = CreateVm(TestData.Planner(), new MemoryData());
        await vm.AddPoint.Execute(TestData.P(40, 150));
        await vm.AddPoint.Execute(TestData.P(200, 150));
        await vm.AddPoint.Execute(TestData.P(300, 150));
        var original = vm.Points.Select(point => point.Id).ToArray();
        await vm.BuildRoutes.Execute();
        Assert.NotEmpty(vm.RouteChoices);
        var originalRouteId = vm.SelectedRoute!.Id;

        await vm.ReorderPoints.Execute(new WaypointReorder(original[0], 2));
        Assert.Equal(new[] { original[1], original[2], original[0] }, vm.Points.Select(point => point.Id));
        Assert.Equal(new[] { 1, 2, 3 }, vm.Points.Select(point => point.Number));
        Assert.NotEmpty(vm.Alternatives);
        Assert.NotEmpty(vm.RouteChoices);
        Assert.NotNull(vm.SelectedRoute);
        Assert.True(vm.HasRoute);
        Assert.NotEqual(originalRouteId, vm.SelectedRoute!.Id);

        vm.SelectedPoint = vm.Points[0];
        await vm.RemovePoint.Execute(original[2]);
        Assert.Equal(new[] { original[1], original[0] }, vm.Points.Select(point => point.Id));
        Assert.Equal(new[] { 1, 2 }, vm.Points.Select(point => point.Number));
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
        var first = vm.SelectedRoute!;
        var other = vm.Alternatives.First(route => route.Id != first.Id);
        await vm.ChooseRoute.Execute(other.Id);
        Assert.Same(other, vm.SelectedRoute);
        Assert.NotEqual(first.Geometry, vm.SelectedRoute!.Geometry);
        Assert.Equal(other.Id, Assert.Single(vm.RouteChoices, choice => choice.IsSelected).Id);
        Assert.Contains("10", vm.MetricsText);
        Assert.Equal(0.10, new RoutingOptions().ReserveFraction);
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
        Assert.Equal(2, planner.Calls);
        Assert.Equal(vm.Points.Select(p => p.Coordinate), planner.LastPoints);
    }

    private sealed class CancelFirstPlanner : IRoutePlanner
    {
        public TaskCompletionSource Started { get; } = new();
        public int Calls
        {
            get; private set;
        }
        public IReadOnlyList<GeoCoordinate>? LastPoints
        {
            get; private set;
        }
        public async Task<IReadOnlyList<RouteAlternative>> PlanAsync(
            IReadOnlyList<GeoCoordinate> points, RoutingOptions options, CancellationToken ct)
        {
            Calls++;
            if (Calls == 1)
            {
                Started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            LastPoints = points.ToArray();
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
}
