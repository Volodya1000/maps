using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using CableRoutePlanner.Bootstrap;
using CableRoutePlanner.UI;
using CableRoutePlanner.UI.Views;
using CableRoutePlanner.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(CableRoutePlanner.Tests.TestApplication))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace CableRoutePlanner.Tests;

public static class TestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<PlannerApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).UseReactiveUI();
}

public sealed class UiTests
{
    [AvaloniaFact]
    public void CompiledXamlAndTypedViewLocatorResolveAllRegisteredScreens()
    {
        using var provider = AppBootstrap.CreateServices(
            TestData.RealData,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"),
            new MemoryClipboard());
        var locator = provider.GetRequiredService<TypedViewLocator>();
        Assert.IsType<MapView>(locator.ResolveView(provider.GetRequiredService<MapViewModel>()));
        Assert.IsType<SettingsView>(locator.ResolveView(provider.GetRequiredService<SettingsViewModel>()));
        var shell = provider.GetRequiredService<ShellView>();
        var window = new Window
        {
            Content = shell
        };
        window.Show();
        Assert.NotNull(shell.ViewModel);
        window.Close();
    }

    [AvaloniaFact]
    public void MapMouseInputAddsAndMovesViewModelPoints()
    {
        using var provider = AppBootstrap.CreateServices(
            TestData.RealData,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"),
            new MemoryClipboard());
        var vm = provider.GetRequiredService<MapViewModel>();
        var view = (MapView)provider.GetRequiredService<TypedViewLocator>().ResolveView(vm)!;
        var window = new Window
        {
            Content = view,
            Width = 1100,
            Height = 800
        };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.MouseDown(new Avalonia.Point(360, 360), Avalonia.Input.MouseButton.Left);
        window.MouseUp(new Avalonia.Point(360, 360), Avalonia.Input.MouseButton.Left);
        Assert.Single(vm.Points);
        var before = vm.Points[0].Coordinate;
        window.MouseDown(new Avalonia.Point(360, 360), Avalonia.Input.MouseButton.Left);
        window.MouseMove(new Avalonia.Point(390, 380));
        window.MouseUp(new Avalonia.Point(390, 380), Avalonia.Input.MouseButton.Left);
        Assert.Single(vm.Points);
        Assert.True(before.DistanceTo(vm.Points[0].Coordinate) > 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AlternativeButtonChangesRenderedPolyline()
    {
        var forest = TestData.Polygon(1, CableRoutePlanner.Domain.TerrainKind.Forest, 100, 60, 220, 240);
        using var vm = new MapViewModel(new NavigationHost(),
            new CableRoutePlanner.Application.PlanningSession(TestData.Planner(forest)),
            new MemoryData(forest), new MemorySettings(), new MemoryClipboard());
        await vm.AddPoint.Execute(TestData.P(30, 150));
        await vm.AddPoint.Execute(TestData.P(300, 150));
        await vm.BuildRoutes.Execute();
        var view = new MapView((control, bounds) => new CableRoutePlanner.UI.MapAdapters.MapsuiMapAdapter(control, bounds))
        {
            ViewModel = vm
        };
        var window = new Window { Content = view, Width = 1100, Height = 1100 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var target = vm.RouteChoices.Last();
        var button = view.GetVisualDescendants().OfType<Button>()
            .Single(candidate => candidate.DataContext is RouteChoice choice && choice.Id == target.Id);
        button.BringIntoView();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var position = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        var map = view.GetVisualDescendants().OfType<Mapsui.UI.Avalonia.MapControl>().Single();
        var layer = (Mapsui.Layers.MemoryLayer)map.Map.Layers.Single(candidate => candidate.Name == "Маршруты");
        var before = ((Mapsui.Nts.GeometryFeature)Assert.Single(layer.Features)).Geometry!.Copy();
        window.MouseDown(position, Avalonia.Input.MouseButton.Left);
        window.MouseUp(position, Avalonia.Input.MouseButton.Left);
        Assert.Equal(target.Id, vm.SelectedRoute?.Id);
        var rendered = (Mapsui.Nts.GeometryFeature)Assert.Single(layer.Features);
        Assert.Equal(target.Id.ToString(), rendered["route_id"]);
        Assert.False(before.EqualsExact(rendered.Geometry));
        window.Close();
    }

    [AvaloniaFact]
    public async Task StartupOpensShellAfterBackgroundDataPreparation()
    {
        var prepared = false;
        using var provider = AppBootstrap.CreateServices(TestData.RealData,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), new MemoryClipboard(),
            async (_, ct) => { await Task.Delay(10, ct); prepared = true; });
        var view = provider.GetRequiredService<StartupView>();
        var window = new Window { Content = view, Width = 900, Height = 700 };
        window.Show();
        for (var attempt = 0; attempt < 100 && view.ViewModel!.Shell is null; attempt++)
        {
            await Task.Delay(10);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
        Assert.True(prepared);
        Assert.NotNull(view.ViewModel!.Shell);
        Assert.NotEmpty(view.GetVisualDescendants().OfType<ShellView>());
        window.Close();
    }

    [AvaloniaFact]
    public async Task FirstOpeningRequestsCountryViewportRatherThanAStartupPatch()
    {
        var repository = new ViewportRepository();
        using var vm = new MapViewModel(new NavigationHost(),
            new CableRoutePlanner.Application.PlanningSession(TestData.Planner()),
            repository, new MemorySettings(), new MemoryClipboard());
        var view = new MapView((control, bounds) => new CableRoutePlanner.UI.MapAdapters.MapsuiMapAdapter(control, bounds))
        {
            ViewModel = vm
        };
        var window = new Window { Content = view, Width = 412, Height = 860 };
        window.Show();
        for (var attempt = 0; attempt < 100 && repository.LastBounds is null; attempt++)
        {
            await Task.Delay(20);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
        Assert.NotNull(repository.LastBounds);
        var bounds = repository.LastBounds!.Value;
        Assert.True(bounds.East - bounds.West >= 9.5);
        Assert.True(repository.Resolution > 250);
        window.Close();
    }

    [AvaloniaFact]
    public void InitialCameraUsesFinalLayoutSizeAndPreservesUserZoom()
    {
        var map = new Mapsui.UI.Avalonia.MapControl();
        var window = new Window { Content = map, Width = 60, Height = 400 };
        window.Show();
        using var adapter = new CableRoutePlanner.UI.MapAdapters.MapsuiMapAdapter(
            map, new(23.17, 51.23, 32.77, 56.18));
        adapter.InitializeViewport();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var provisional = map.Map.Navigator.Viewport.Resolution;
        window.Width = 412;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(map.Map.Navigator.Viewport.Resolution < provisional / 2);
        adapter.ZoomIn();
        var zoom = map.Map.Navigator.Viewport.Resolution;
        window.Width = 800;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(zoom, map.Map.Navigator.Viewport.Resolution);
        window.Close();
    }

    private sealed class ViewportRepository : CableRoutePlanner.Application.IGeodataRepository
    {
        public CableRoutePlanner.Application.DataPackageInfo Info
        {
            get;
        } = new(
            "synthetic-viewport-test", new(23.17, 51.23, 32.77, 56.18), "test", false, "test");

        public CableRoutePlanner.Domain.GeoBounds? LastBounds
        {
            get; private set;
        }

        public double Resolution
        {
            get; private set;
        }

        public Task<IReadOnlyList<CableRoutePlanner.Application.MapFeature>> QueryAsync(
            CableRoutePlanner.Domain.GeoBounds bounds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CableRoutePlanner.Application.MapFeature>>([]);

        public Task<IReadOnlyList<CableRoutePlanner.Application.MapFeature>> QueryMapAsync(
            CableRoutePlanner.Domain.GeoBounds bounds, double resolution, CancellationToken ct)
        {
            LastBounds = bounds;
            Resolution = resolution;
            return QueryAsync(bounds, ct);
        }
    }

    [AvaloniaFact]
    public void UnknownViewModelCannotFallBackToReflection()
    {
        using var provider = AppBootstrap.CreateServices(
            TestData.RealData,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"),
            new MemoryClipboard());
        Assert.Throws<ArgumentException>(
            () => provider.GetRequiredService<TypedViewLocator>().ResolveView(new object()));
    }
}
