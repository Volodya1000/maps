using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Persistence;
using CableRoutePlanner.ViewModels;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class SavedRoutesTests : ReactiveTestScope
{
    [Fact]
    public async Task SaveAndOpenCommandsRestoreAllVariantsPointsSelectionAndFrozenMetricsAfterRestart()
    {
        using SavedRouteDirectory directory = new();
        SavedRoute expected = SavedRouteDirectory.Snapshot();
        MemorySettings settings = new();
        using (MapViewModel map = CreateMap(directory, new SnapshotPlanner(expected), settings: settings))
        {
            foreach (Waypoint point in expected.Points)
            {
                await map.AddPoint.Execute(point.Coordinate).ToTask();
            }
            await map.BuildRoutes.Execute().ToTask();
            await map.ChooseRoute.Execute(expected.SelectedAlternativeId).ToTask();
            map.RouteName = "Расчёт";
            await map.SaveRoute.Execute().ToTask();
            await map.ClearPoints.Execute().ToTask();
        }
        settings.Save(new RoutingOptions(ReserveFraction: 0.2));
        SavedRoute saved = Assert.Single(directory.Store.List());
        NavigationHost screen = new();
        using MapViewModel restored = CreateMap(directory, new RejectPlanner(), screen, settings);
        using SettingsViewModel settingsVm = new(screen, settings);
        using SavedRoutesViewModel list = new(screen, restored, directory.Store);
        using ShellViewModel shell = new(screen, restored, settingsVm, list);
        await shell.ShowSaved.Execute().ToTask();
        await list.Open.Execute(saved.Id).ToTask();
        Assert.True(shell.IsMap);
        Assert.Equal(saved.Points, restored.Points.Select(point => new Waypoint(point.Id, point.Coordinate)));
        Assert.Equal(JsonSerializer.Serialize(expected.Alternatives), JsonSerializer.Serialize(restored.Alternatives));
        Assert.Equal(expected.SelectedAlternativeId, restored.SelectedRoute!.Id);
        Assert.Equal(RouteMetricsFormatter.Format(expected.Alternatives[1], expected.Options), restored.MetricsText);
        Assert.Null(restored.ViewportState);
        await restored.ChooseRoute.Execute(expected.Alternatives[0].Id).ToTask();
        Assert.Equal(expected.Alternatives[0].Id, restored.SelectedRoute!.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SingleAlternativeCanBeSavedWithBlankNameAndRecognizedAfterRestart(string name)
    {
        using SavedRouteDirectory directory = new();
        SavedRoute snapshot = SavedRouteDirectory.Snapshot();
        snapshot = snapshot with { Alternatives = [snapshot.Alternatives[0]], SelectedAlternativeId = snapshot.Alternatives[0].Id };
        using MapViewModel map = CreateMap(directory, new SnapshotPlanner(snapshot));
        foreach (Waypoint point in snapshot.Points)
        {
            await map.AddPoint.Execute(point.Coordinate).ToTask();
        }
        await map.BuildRoutes.Execute().ToTask();
        Assert.False(map.HasAlternatives);
        map.RouteName = name;
        await map.SaveRoute.Execute().ToTask();
        SavedRoute saved = Assert.Single(directory.Store.List());
        SavedRouteRow row = new(saved);
        Assert.StartsWith("Маршрут от ", row.Title, StringComparison.Ordinal);
        Assert.Equal(saved.AddedUtc.ToLocalTime().ToString("dd.MM.yyyy, HH:mm", System.Globalization.CultureInfo.CurrentCulture), row.AddedText);
        using MapViewModel restored = CreateMap(directory, new RejectPlanner());
        Assert.True(restored.OpenSavedRoute(saved.Id));
        Assert.Single(restored.Alternatives);
        Assert.Equal(snapshot.Alternatives[0].Geometry, restored.SelectedRoute!.Geometry);
    }

    [Fact]
    public async Task OpeningSnapshotInvalidatesAnUnfinishedCalculationEvenWhenPlannerIgnoresCancellation()
    {
        using SavedRouteDirectory directory = new();
        SavedRoute saved = SavedRouteDirectory.Snapshot();
        directory.Store.Add(saved);
        DeferredPlanner planner = new();
        using MapViewModel map = CreateMap(directory, planner);
        await map.AddPoint.Execute(TestData.P(0, 0)).ToTask();
        await map.AddPoint.Execute(TestData.P(30, 0)).ToTask();
        Task calculation = map.BuildRoutes.Execute().ToTask();
        Assert.True(map.IsBusy);
        Assert.True(map.OpenSavedRoute(saved.Id));
        planner.Completion.SetResult([saved.Alternatives[0] with { Id = Guid.NewGuid() }]);
        await calculation;
        Assert.Equal(saved.SelectedAlternativeId, map.SelectedRoute!.Id);
        Assert.Equal(saved.Points, map.Points.Select(point => new Waypoint(point.Id, point.Coordinate)));
        Assert.Equal(saved.Alternatives.Count, map.Alternatives.Count);
    }

    [Fact]
    public async Task PendingReorderCannotStartANewCalculationAfterOpeningSavedRoute()
    {
        using SavedRouteDirectory directory = new();
        SavedRoute saved = SavedRouteDirectory.Snapshot();
        directory.Store.Add(saved);
        DeferredPlanner planner = new();
        using MapViewModel map = CreateMap(directory, planner);
        await map.AddPoint.Execute(TestData.P(0, 0)).ToTask();
        await map.AddPoint.Execute(TestData.P(30, 0)).ToTask();
        Task calculation = map.BuildRoutes.Execute().ToTask();
        Task reorder = map.ReorderPoints.Execute(new(map.Points[0].Id, 1)).ToTask();
        Assert.True(map.OpenSavedRoute(saved.Id));
        planner.Completion.SetResult([saved.Alternatives[0]]);
        await Task.WhenAll(calculation, reorder);
        Assert.Equal(saved.SelectedAlternativeId, map.SelectedRoute!.Id);
        Assert.Equal(saved.Points, map.Points.Select(point => new Waypoint(point.Id, point.Coordinate)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingPlacementCannotChangeOpenedSnapshot(bool move)
    {
        using SavedRouteDirectory directory = new();
        SavedRoute saved = SavedRouteDirectory.Snapshot();
        directory.Store.Add(saved);
        DeferredPlacement validator = new();
        using MapViewModel map = CreateMap(directory, new RejectPlanner(), validator: validator);
        Assert.True(map.OpenSavedRoute(saved.Id));
        Task placement = move
            ? map.MovePointAsync(map.Points[0].Id, TestData.P(300, 300))
            : map.AddPoint.Execute(TestData.P(300, 300)).ToTask();
        Assert.True(map.OpenSavedRoute(saved.Id));
        RouteAlternative route = map.SelectedRoute!;
        string metrics = map.MetricsText;
        validator.Completion.SetResult();
        await placement;
        Assert.Same(route, map.SelectedRoute);
        Assert.Equal(metrics, map.MetricsText);
        Assert.Equal(saved.Points, map.Points.Select(point => new Waypoint(point.Id, point.Coordinate)));
        Assert.Equal(saved.Alternatives.Count, map.Alternatives.Count);
    }

    [Fact]
    public async Task FailedReadKeepsEntireWorkingRouteAndNavigation()
    {
        using SavedRouteDirectory directory = new();
        SavedRoute saved = SavedRouteDirectory.Snapshot();
        directory.Store.Add(saved);
        NavigationHost screen = new();
        using MapViewModel map = CreateMap(directory, new RejectPlanner(), screen);
        Assert.True(map.OpenSavedRoute(saved.Id));
        map.UpdateViewportState(new(1, 2, 3, 4));
        RouteAlternative? before = map.SelectedRoute;
        string metrics = map.MetricsText;
        using SavedRoutesViewModel list = new(screen, map, directory.Store);
        using SettingsViewModel settings = new(screen, new MemorySettings());
        using ShellViewModel shell = new(screen, map, settings, list);
        await shell.ShowSaved.Execute().ToTask();
        File.WriteAllText(directory.Path, "{broken");
        await list.Open.Execute(saved.Id).ToTask();
        Assert.True(shell.IsSaved);
        Assert.Same(before, map.SelectedRoute);
        Assert.Equal(metrics, map.MetricsText);
        Assert.Equal(saved.Points.Count, map.Points.Count);
        Assert.Equal(new MapViewportState(1, 2, 3, 4), map.ViewportState);
        Assert.NotEmpty(list.Status);
    }

    [Theory]
    [InlineData("metric")]
    [InlineData("coordinate")]
    [InlineData("options")]
    [InlineData("transition")]
    public async Task MissingSnapshotMembersAreRejectedWithoutReplacingWorkingRoute(string field)
    {
        using SavedRouteDirectory directory = new();
        SavedRoute saved = SavedRouteDirectory.Snapshot();
        directory.Store.Add(saved);
        using MapViewModel map = CreateMap(directory, new RejectPlanner());
        Assert.True(map.OpenSavedRoute(saved.Id));
        RouteAlternative current = map.SelectedRoute!;
        string metrics = map.MetricsText;
        JsonNode root = JsonNode.Parse(File.ReadAllText(directory.Path))!;
        JsonNode snapshot = root["Routes"]![0]!;
        switch (field)
        {
            case "metric":
                snapshot["Alternatives"]![0]!["Metrics"]!.AsObject().Remove("LengthMetres");
                break;
            case "coordinate":
                snapshot["Points"]![0]!.AsObject().Remove("Coordinate");
                break;
            case "options":
                snapshot["Options"]!.AsObject().Remove("ReserveFraction");
                break;
            case "transition":
                snapshot["Alternatives"]![0]!.AsObject().Remove("TransitionPoints");
                break;
        }
        string broken = root.ToJsonString();
        File.WriteAllText(directory.Path, broken);
        using SavedRoutesViewModel list = new(new NavigationHost(), map, directory.Store);
        Assert.False(await list.Open.Execute(saved.Id).ToTask());
        Assert.Same(current, map.SelectedRoute);
        Assert.Equal(metrics, map.MetricsText);
        Assert.Equal(saved.Points, map.Points.Select(point => new Waypoint(point.Id, point.Coordinate)));
        Assert.Equal(broken, File.ReadAllText(directory.Path));
    }

    [Fact]
    public async Task DeleteRequiresConfirmationForExactRecordAndSettingsGuardRemainsActive()
    {
        using SavedRouteDirectory directory = new();
        SavedRoute first = SavedRouteDirectory.Snapshot();
        SavedRoute second = first with { Id = Guid.NewGuid() };
        directory.Store.Add(first);
        directory.Store.Add(second);
        NavigationHost screen = new();
        using MapViewModel map = CreateMap(directory, new RejectPlanner(), screen);
        using SavedRoutesViewModel list = new(screen, map, directory.Store);
        using SettingsViewModel settings = new(screen, new MemorySettings());
        using ShellViewModel shell = new(screen, map, settings, list);
        await shell.ShowSettings.Execute().ToTask();
        settings.ReservePercent = 20;
        await shell.ShowSaved.Execute().ToTask();
        Assert.True(shell.IsSettings);
        Assert.True(shell.ShowUnsavedWarning);
        await shell.DiscardAndLeave.Execute().ToTask();
        Assert.True(shell.IsSaved);
        await list.RequestDelete.Execute(first.Id).ToTask();
        Assert.Equal(2, directory.Store.List().Count);
        Assert.Equal(first.Id, list.PendingDelete!.Id);
        await list.CancelDelete.Execute().ToTask();
        Assert.Null(list.PendingDelete);
        await list.RequestDelete.Execute(first.Id).ToTask();
        await list.ConfirmDelete.Execute().ToTask();
        Assert.Equal(second.Id, Assert.Single(directory.Store.List()).Id);
    }

    internal static MapViewModel CreateMap(SavedRouteDirectory directory, IRoutePlanner planner,
        NavigationHost? screen = null, MemorySettings? settings = null, ITrackingViewModel? tracking = null, IPointPlacementValidator? validator = null)
    {
        PlanningSession session = new(planner, validator ?? TestData.AllowAllValidator);
        return new(screen ?? new NavigationHost(), session, new MapAreaLoader(new MemoryData()),
            new RouteCalculationService(session, settings ?? new MemorySettings(), () => new MemoryData().Info.Bounds),
            new MemoryClipboard(), tracking, directory.Store);
    }

    private sealed class SnapshotPlanner(SavedRoute snapshot) : IRoutePlanner
    {
        public Task<IReadOnlyList<RouteAlternative>> PlanAsync(IReadOnlyList<GeoCoordinate> points,
            RoutingOptions options, CancellationToken cancellationToken) => Task.FromResult(snapshot.Alternatives);
    }

    internal sealed class RejectPlanner : IRoutePlanner
    {
        public Task<IReadOnlyList<RouteAlternative>> PlanAsync(IReadOnlyList<GeoCoordinate> points,
            RoutingOptions options, CancellationToken cancellationToken) => throw new InvalidOperationException("Saved routes must not invoke planning.");
    }

    private sealed class DeferredPlacement : IPointPlacementValidator
    {
        public TaskCompletionSource Completion { get; } = new();
        public Task ValidateAsync(GeoCoordinate coordinate, CancellationToken cancellationToken) => Completion.Task;
    }

    private sealed class DeferredPlanner : IRoutePlanner
    {
        public TaskCompletionSource<IReadOnlyList<RouteAlternative>> Completion { get; } = new();
        public Task<IReadOnlyList<RouteAlternative>> PlanAsync(IReadOnlyList<GeoCoordinate> points,
            RoutingOptions options, CancellationToken cancellationToken) => Completion.Task;
    }
}
