using System.Reactive.Threading.Tasks;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure;
using CableRoutePlanner.ViewModels;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class MapViewModelRefactoringTests : ReactiveTestScope
{
    private static readonly IReadOnlyList<int> ThreePointNumbers = Array.AsReadOnly(new[] { 1, 2, 3 });

    [Fact]
    public void UnexpectedErrorHasUserMessageAndDoesNotUpdateDisposedScreen()
    {
        MapViewModel vm = CreateVm(new MemoryData());
        vm.ReportUnexpectedError(new InvalidOperationException("internal details"));
        Assert.Contains("внутренней ошибки", vm.Status);
        Assert.DoesNotContain("internal details", vm.Status);
        string status = vm.Status;
        vm.Dispose();
        vm.ReportUnexpectedError(new InvalidOperationException("later"));
        Assert.Equal(status, vm.Status);
    }

    [Fact]
    public async Task LatestMapRequestWinsWhenOlderRequestFinishesLast()
    {
        var data = new DeferredMapData();
        using MapViewModel vm = CreateVm(data);
        Task oldRequest = vm.LoadMapAreaAsync(data.Info.Bounds, CancellationToken.None);
        Task newRequest = vm.LoadMapAreaAsync(data.Info.Bounds, CancellationToken.None);

        data.Complete(1, new MapFeature(2, TerrainKind.Field, [], "new"));
        await newRequest;
        data.Complete(0, new MapFeature(1, TerrainKind.Field, [], "old"));
        await oldRequest;

        Assert.Equal(2L, Assert.Single(vm.Features).Id);
        Assert.Empty(vm.MapStatus);
        Assert.False(vm.HasMapNotice);
    }

    [Fact]
    public async Task SuccessfulMapReloadPublishesCompleteSnapshotWithoutChangingPreviousOne()
    {
        var data = new DeferredMapData();
        using MapViewModel vm = CreateVm(data);
        Task firstLoad = vm.LoadMapAreaAsync(data.Info.Bounds, CancellationToken.None);
        data.Complete(0,
            new MapFeature(1, TerrainKind.Field, [], "first"),
            new MapFeature(2, TerrainKind.Road, [], "second"));
        await firstLoad;

        IReadOnlyList<MapFeature> firstSnapshot = vm.Features;
        int firstVersion = vm.MapVersion;
        Task secondLoad = vm.LoadMapAreaAsync(data.Info.Bounds, CancellationToken.None);
        data.Complete(1, new MapFeature(3, TerrainKind.River, [], "replacement"));
        await secondLoad;

        Assert.Equal(new long[] { 1, 2 }, firstSnapshot.Select(feature => feature.Id));
        Assert.Equal(3L, Assert.Single(vm.Features).Id);
        Assert.Equal(firstVersion + 1, vm.MapVersion);
    }

    [Fact]
    public async Task ObsoleteMapFailureCannotOverwriteSuccessfulNewerRequest()
    {
        var data = new DeferredMapData();
        using MapViewModel vm = CreateVm(data);
        Task oldRequest = vm.LoadMapAreaAsync(data.Info.Bounds, CancellationToken.None);
        Task newRequest = vm.LoadMapAreaAsync(data.Info.Bounds, CancellationToken.None);

        data.Complete(1, new MapFeature(2, TerrainKind.Field, [], "new"));
        await newRequest;
        string status = vm.MapStatus;
        data.Fail(0, new InvalidOperationException("obsolete failure"));
        await oldRequest;

        Assert.Equal(status, vm.MapStatus);
        Assert.Equal(2L, Assert.Single(vm.Features).Id);
    }

    [Fact]
    public async Task DisposalPreventsPendingMapDataFromBeingPublished()
    {
        var data = new DeferredMapData();
        MapViewModel vm = CreateVm(data);
        Task load = vm.LoadMapAreaAsync(data.Info.Bounds, CancellationToken.None);
        vm.Dispose();

        data.Complete(0, new MapFeature(1, TerrainKind.Field, [], "late"));
        await load;

        Assert.Empty(vm.Features);
    }

    [Fact]
    public async Task CanceledMapRequestDoesNotReplaceDisplayedFeatures()
    {
        var data = new DeferredMapData();
        using MapViewModel vm = CreateVm(data);
        using var cancellation = new CancellationTokenSource();
        Task initialLoad = vm.LoadMapAreaAsync(data.Info.Bounds, CancellationToken.None);
        data.Complete(0, new MapFeature(1, TerrainKind.Field, [], "initial"));
        await initialLoad;

        Task load = vm.LoadMapAreaAsync(data.Info.Bounds, cancellation.Token);
        cancellation.Cancel();
        data.Complete(1, new MapFeature(2, TerrainKind.Field, [], "canceled"));
        await load;

        Assert.Equal(1L, Assert.Single(vm.Features).Id);
    }

    [Fact]
    public async Task EmptySuccessfulMapRequestShowsMissingAreaWithoutError()
    {
        var data = new DeferredMapData();
        using MapViewModel vm = CreateVm(data);
        Task load = vm.LoadMapAreaAsync(data.Info.Bounds, CancellationToken.None);

        data.Complete(0);
        await load;

        Assert.Empty(vm.Features);
        Assert.Equal("Для этой области нет данных в офлайн-пакете.", vm.MapStatus);
    }

    [Fact]
    public async Task FailedMapRequestKeepsPreviouslyLoadedFeatures()
    {
        var data = new DeferredMapData();
        using MapViewModel vm = CreateVm(data);
        Task initialLoad = vm.LoadMapAreaAsync(data.Info.Bounds, CancellationToken.None);
        data.Complete(0, new MapFeature(1, TerrainKind.Field, [], "initial"));
        await initialLoad;

        Task failedLoad = vm.LoadMapAreaAsync(data.Info.Bounds, CancellationToken.None);
        data.Fail(1, new InvalidOperationException("repository unavailable"));
        await failedLoad;

        Assert.Equal(1L, Assert.Single(vm.Features).Id);
        Assert.Equal("Ошибка загрузки карты: repository unavailable", vm.MapStatus);
    }

    [Fact]
    public async Task PointProjectionKeepsSelectionAndRenumbersAfterReordering()
    {
        using MapViewModel vm = CreateVm(new MemoryData());
        await vm.AddPoint.Execute(TestData.P(50, 150)).ToTask();
        await vm.AddPoint.Execute(TestData.P(100, 150)).ToTask();
        await vm.AddPoint.Execute(TestData.P(150, 150)).ToTask();
        Guid selectedId = vm.Points[0].Id;
        vm.SelectedPoint = vm.Points[0];

        await vm.ReorderPoints.Execute(new WaypointReorder(selectedId, 2)).ToTask();

        Assert.Equal(selectedId, vm.SelectedPoint?.Id);
        Assert.Equal(selectedId, vm.Points[2].Id);
        Assert.Equal(ThreePointNumbers, vm.Points.Select(point => point.Number));
        Assert.Equal(3, vm.PointCount);
    }

    [Fact]
    public async Task ManualInputRejectsOutOfRangeCoordinateWithoutAddingWaypoint()
    {
        using MapViewModel vm = CreateVm(new MemoryData());
        vm.LatitudeText = "95";
        vm.LongitudeText = "27.5";

        await vm.AddManual.Execute().ToTask();

        Assert.Empty(vm.Points);
        Assert.Equal("Некорректные координаты.", vm.Status);
    }

    [Fact]
    public async Task ManualInputAddsValidWaypointInsideOfflinePackage()
    {
        using MapViewModel vm = CreateVm(new MemoryData());
        vm.LatitudeText = "54.001";
        vm.LongitudeText = "27.001";

        await vm.AddManual.Execute().ToTask();

        WaypointRow point = Assert.Single(vm.Points);
        Assert.Equal(new GeoCoordinate(54.001, 27.001), point.Coordinate);
        Assert.Equal(1, vm.PointCount);
    }

    [Fact]
    public async Task ManualInputRejectsCoordinateOutsideOfflinePackage()
    {
        using MapViewModel vm = CreateVm(new MemoryData());
        vm.LatitudeText = "53.5";
        vm.LongitudeText = "27.001";

        await vm.AddManual.Execute().ToTask();

        Assert.Empty(vm.Points);
        Assert.Equal("Для этой точки нет данных в офлайн-пакете.", vm.Status);
    }

    [Fact]
    public async Task InvalidPointMoveKeepsSessionPointAndSelection()
    {
        MapFeature building = TestData.Polygon(1, TerrainKind.Building, 90, 140, 110, 160);
        var data = new MemoryData(building);
        var session = new PlanningSession(
            TestData.Planner(building), new GeodataPointPlacementValidator(data));
        using MapViewModel vm = TestData.CreateMapViewModel(
            new NavigationHost(), session, new MapAreaLoader(data), new MemorySettings(), new MemoryClipboard());
        await vm.AddPoint.Execute(TestData.P(50, 150)).ToTask();
        WaypointRow original = Assert.Single(vm.Points);
        long revision = session.Revision;

        await vm.MovePointAsync(original.Id, TestData.P(100, 150));

        Assert.Equal(original, Assert.Single(vm.Points));
        Assert.Equal(original.Id, vm.SelectedPoint?.Id);
        Assert.Equal(revision, session.Revision);
        Assert.Contains("здания", vm.Status);
    }

    [Fact]
    public void RouteMetricsFormattingUsesTheSettingsForTheActualCalculation()
    {
        var route = new RouteAlternative(
            Guid.NewGuid(),
            "test",
            [],
            new RouteMetrics(100, 40, 60, 1, 2, 0, 130, null));
        string result = RouteMetricsFormatter.Format(route, new RoutingOptions(ReserveFraction: 0.25));

        Assert.Contains($"Кабель с запасом {0.25:P0}: {125:N0} м", result);
        Assert.Contains("Пересечённых дорог: 1", result);
        Assert.Contains("Переходов по мостам: 0", result);
        Assert.DoesNotContain("Высотные характеристики", result);
        Assert.DoesNotContain("Требуются обследование и согласование.", result);
    }

    private static MapViewModel CreateVm(IGeodataRepository data) => TestData.CreateMapViewModel(
        new NavigationHost(),
        new PlanningSession(TestData.Planner(), TestData.AllowAllValidator),
        new MapAreaLoader(data),
        new MemorySettings(),
        new MemoryClipboard());

    private sealed class DeferredMapData : IGeodataRepository
    {
        private readonly List<TaskCompletionSource<IReadOnlyList<MapFeature>>> _requests = [];

        public DataPackageInfo Info { get; } = new(
            "test", new GeoBounds(27, 54, 28, 55), "test", false, "test");

        public Task<IReadOnlyList<MapFeature>> QueryAsync(
            GeoBounds bounds,
            CancellationToken cancellationToken) =>
            QueryMapAsync(bounds, 0, cancellationToken);

        public Task<IReadOnlyList<MapFeature>> QueryMapAsync(
            GeoBounds bounds,
            double metresPerPixel,
            CancellationToken cancellationToken)
        {
            var request = new TaskCompletionSource<IReadOnlyList<MapFeature>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _requests.Add(request);
            return request.Task;
        }

        public void Complete(int index, params MapFeature[] features) =>
            _requests[index].SetResult(features);

        public void Fail(int index, Exception error) =>
            _requests[index].SetException(error);
    }
}
