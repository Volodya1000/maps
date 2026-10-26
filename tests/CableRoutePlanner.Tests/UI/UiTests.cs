using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using System.Diagnostics;
using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using CableRoutePlanner.Application;
using CableRoutePlanner.Infrastructure;
using CableRoutePlanner.Persistence;
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

    public static ServiceProvider CreateServices(
        string dataDirectory,
        string settingsPath,
        IClipboardService? clipboard = null,
        Func<IProgress<string>, CancellationToken, Task>? prepareData = null)
    {
        var services = new ServiceCollection();
        services.AddInfrastructure();
        services.AddPersistence(dataDirectory, settingsPath);
        services.AddSingleton<IPlanningSession, PlanningSession>();
        services.AddPresentation(prepareData, clipboard);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public static ServiceProvider CreateCoreServices(string dataDirectory, string settingsPath)
    {
        var services = new ServiceCollection();
        services.AddInfrastructure();
        services.AddPersistence(dataDirectory, settingsPath);
        services.AddSingleton<IPlanningSession, PlanningSession>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}

public sealed class UiTests
{
    [AvaloniaFact]
    public async Task ExtractedStylesKeepMapAndWaypointAppearance()
    {
        using var vm = new MapViewModel(new NavigationHost(),
            new CableRoutePlanner.Application.PlanningSession(TestData.Planner(), TestData.AllowAllValidator),
            new MemoryData(), new MemorySettings(), new MemoryClipboard());
        await vm.AddPoint.Execute(TestData.P(30, 150));
        await vm.AddPoint.Execute(TestData.P(300, 150));
        var view = new MapView((control, bounds) => new CableRoutePlanner.UI.MapAdapters.MapsuiMapAdapter(control, bounds))
        {
            ViewModel = vm
        };
        var window = new Window { Content = view, Width = 1100, Height = 900 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var compass = view.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Name == "NorthButton");
        Assert.Equal(46d, compass.Width);
        Assert.Equal(46d, compass.Height);
        Assert.Equal(Avalonia.Media.Color.Parse("#EEFFFFFF"),
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(compass.Background).Color);

        var waypoints = view.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Name == "CoordinateCard").ToArray();
        Assert.Equal(2, waypoints.Length);
        var selected = Assert.Single(waypoints,
            border => border.DataContext is WaypointRow { IsSelected: true });
        var unselected = Assert.Single(waypoints,
            border => border.DataContext is WaypointRow { IsSelected: false });

        Assert.Equal(new CornerRadius(8), selected.CornerRadius);
        Assert.Equal(new Thickness(2), selected.BorderThickness);
        Assert.Equal(Avalonia.Media.Color.Parse("#DBEAFB"),
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(selected.Background).Color);
        Assert.Equal(Avalonia.Media.Color.Parse("#1A6FD2"),
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(selected.BorderBrush).Color);

        Assert.Equal(new CornerRadius(8), unselected.CornerRadius);
        Assert.Equal(new Thickness(1), unselected.BorderThickness);
        Assert.Equal(Avalonia.Media.Color.Parse("#F1F5F2"),
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(unselected.Background).Color);
        window.Close();
    }

    [AvaloniaFact]
    public void MapAdapterRendersEveryTerrainCategory()
    {
        // Arrange
        var control = new Mapsui.UI.Avalonia.MapControl();
        using var adapter = new CableRoutePlanner.UI.MapAdapters.MapsuiMapAdapter(
            control,
            new CableRoutePlanner.Domain.GeoBounds(27, 53, 28, 54));
        var kinds = Enum.GetValues<CableRoutePlanner.Domain.TerrainKind>();
        var features = kinds.Select((kind, index) => TestData.Polygon(
            index + 1,
            kind,
            10 + index * 3,
            10,
            12 + index * 3,
            12)).ToArray();

        // Act
        adapter.SetFeatures(features);

        // Assert
        var rendered = Assert.Single(control.Map.Layers.OfType<Mapsui.Layers.MemoryLayer>(),
            layer => layer.Name == "Офлайн OSM");
        Assert.Equal(kinds.Length, rendered.Features!.Count());
        Assert.All(rendered.Features, feature => Assert.NotEmpty(feature.Styles));
    }

    [AvaloniaFact]
    public async Task SystemBackAndWindowCloseWarnAboutUnsavedSettings()
    {
        using var provider = TestApplication.CreateServices(TestData.RealData,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), new MemoryClipboard());
        var shell = provider.GetRequiredService<ShellView>();
        var vm = shell.ViewModel!;
        var settings = provider.GetRequiredService<SettingsViewModel>();
        var window = new Window { Content = shell, Width = 900, Height = 700 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await vm.ShowSettings.Execute();
        settings.CellSize = 40;
        var back = new Avalonia.Interactivity.RoutedEventArgs(TopLevel.BackRequestedEvent);
        window.RaiseEvent(back);
        Assert.True(back.Handled);
        Assert.True(vm.IsSettings);
        Assert.True(vm.ShowUnsavedWarning);
        await vm.StayOnSettings.Execute();
        window.Close();
        Assert.True(window.IsVisible);
        Assert.True(vm.ShowUnsavedWarning);
        await vm.StayOnSettings.Execute();
        Assert.Equal(40m, settings.CellSize);
        window.Close();
        await vm.DiscardAndLeave.Execute();
        Assert.False(window.IsVisible);
        Assert.True(settings.HasChanges);
    }

    [AvaloniaFact]
    public async Task SettingsInputsShowSavedValuesAndSaveButtonTracksEdits()
    {
        using var vm = new SettingsViewModel(new NavigationHost(), new MemorySettings());
        var view = new SettingsView { ViewModel = vm };
        var window = new Window { Content = view, Width = 600, Height = 700 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var inputs = view.GetVisualDescendants().OfType<NumericUpDown>().ToArray();
        Assert.Equal(20m, inputs[0].Value);
        Assert.Equal(10m, inputs[1].Value);
        Assert.NotEmpty(inputs[0].GetVisualDescendants().OfType<TextBox>().Single().Text!);
        Assert.NotEmpty(inputs[1].GetVisualDescendants().OfType<TextBox>().Single().Text!);
        var save = view.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Сохранить"));
        Assert.False(save.IsEffectivelyEnabled);
        inputs[0].Value = 40;
        Assert.Equal(40m, vm.CellSize);
        Assert.True(save.IsEffectivelyEnabled);
        inputs[0].Value = 20;
        Assert.False(save.IsEffectivelyEnabled);
        inputs[1].Value = 15;
        await vm.Save.Execute();
        Assert.False(save.IsEffectivelyEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task NavigatingBetweenSettingsAndMapKeepsSettingsValuesVisible()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        using var provider = TestApplication.CreateServices(TestData.RealData, settingsPath, new MemoryClipboard());
        var shell = provider.GetRequiredService<ShellView>();
        var settings = provider.GetRequiredService<SettingsViewModel>();
        var window = new Window { Content = shell, Width = 900, Height = 700 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        await shell.ViewModel!.ShowSettings.Execute();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var inputs = shell.GetVisualDescendants().OfType<NumericUpDown>().ToArray();
        var cellSize = inputs[0].Value;
        var reservePercent = inputs[1].Value;
        Assert.NotNull(cellSize);
        Assert.NotNull(reservePercent);

        await shell.ViewModel.ShowMap.Execute();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(cellSize, settings.CellSize);
        Assert.Equal(reservePercent, settings.ReservePercent);

        await shell.ViewModel.ShowSettings.Execute();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        inputs = shell.GetVisualDescendants().OfType<NumericUpDown>().ToArray();
        Assert.Equal(cellSize, inputs[0].Value);
        Assert.Equal(reservePercent, inputs[1].Value);
        Assert.Null(shell.FindControl<Avalonia.Controls.TransitioningContentControl>("RouteHost")!.PageTransition);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AlternativesAreHiddenUntilRouteIsBuiltAndAfterClear()
    {
        using var vm = new MapViewModel(new NavigationHost(),
            new CableRoutePlanner.Application.PlanningSession(TestData.Planner(), TestData.AllowAllValidator),
            new MemoryData(), new MemorySettings(), new MemoryClipboard());
        var view = new MapView((control, bounds) => new CableRoutePlanner.UI.MapAdapters.MapsuiMapAdapter(control, bounds))
        {
            ViewModel = vm
        };
        var window = new Window { Content = view, Width = 1100, Height = 900 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var choices = view.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "RouteChoices");
        var section = choices.GetVisualAncestors().OfType<StackPanel>().First();
        Assert.False(section.IsVisible);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "3D"));
        var attribution = view.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Name == "AttributionCredit");
        Assert.Contains("OpenStreetMap contributors", attribution.GetValue(Avalonia.Automation.AutomationProperties.NameProperty)!.ToString());
        await vm.AddPoint.Execute(TestData.P(30, 150));
        await vm.AddPoint.Execute(TestData.P(300, 150));
        Assert.False(section.IsVisible);
        await vm.BuildRoutes.Execute();
        Assert.True(section.IsVisible);
        Assert.False(choices.IsVisible);
        Assert.Equal("Маршрут", Assert.Single(vm.RouteChoices).Name);
        await vm.ClearPoints.Execute();
        Assert.False(section.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void CompiledXamlAndTypedViewLocatorResolveAllRegisteredScreens()
    {
        using var provider = TestApplication.CreateServices(
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
    public async Task MapMouseInputAddsAndMovesViewModelPoints()
    {
        using var provider = TestApplication.CreateServices(
            TestData.RealData,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"),
            new MemoryClipboard());
        var vm = provider.GetRequiredService<MapViewModel>();
        var session = provider.GetRequiredService<CableRoutePlanner.Application.IPlanningSession>();
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
        await WaitForRevisionAsync(session, 1, () => vm.Points.Count == 1);
        Assert.Single(vm.Points);
        var before = vm.Points[0].Coordinate;
        var revision = session.Revision;
        window.MouseDown(new Avalonia.Point(360, 360), Avalonia.Input.MouseButton.Left);
        window.MouseMove(new Avalonia.Point(390, 380));
        window.MouseUp(new Avalonia.Point(390, 380), Avalonia.Input.MouseButton.Left);
        await WaitForRevisionAsync(session, revision + 1,
            () => vm.Points.Count == 1 && before.DistanceTo(vm.Points[0].Coordinate) > 1);
        Assert.Single(vm.Points);
        Assert.True(before.DistanceTo(vm.Points[0].Coordinate) > 1,
            $"Point was not moved. Status: {vm.Status}; before: {before}; after: {vm.Points[0].Coordinate}");
        window.Close();
    }

    private static async Task WaitForRevisionAsync(
        CableRoutePlanner.Application.IPlanningSession session,
        long expectedRevision,
        Func<bool>? presentationReady = null)
    {
        var timeout = Stopwatch.StartNew();
        while ((session.Revision < expectedRevision || presentationReady?.Invoke() == false)
            && timeout.Elapsed < TimeSpan.FromSeconds(2))
        {
            await Task.Delay(5);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        Assert.True(session.Revision >= expectedRevision,
            $"PlanningSession did not reach revision {expectedRevision}.");
    }

    [AvaloniaFact]
    public async Task AlternativeButtonChangesRenderedPolyline()
    {
        var forest = TestData.Polygon(1, CableRoutePlanner.Domain.TerrainKind.Forest, 100, 60, 220, 240);
        using var vm = new MapViewModel(new NavigationHost(),
            new CableRoutePlanner.Application.PlanningSession(TestData.Planner(forest), TestData.AllowAllValidator),
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
        using var provider = TestApplication.CreateServices(TestData.RealData,
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
            new CableRoutePlanner.Application.PlanningSession(TestData.Planner(), TestData.AllowAllValidator),
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
    public async Task MobileMapPanelCanBeCollapsedAndSwipedOpenAgain()
    {
        using var vm = new MapViewModel(new NavigationHost(),
            new CableRoutePlanner.Application.PlanningSession(TestData.Planner(), TestData.AllowAllValidator),
            new MemoryData(), new MemorySettings(), new MemoryClipboard());
        var view = new MapView((control, bounds) => new CableRoutePlanner.UI.MapAdapters.MapsuiMapAdapter(control, bounds))
        {
            ViewModel = vm
        };
        var window = new Window { Content = view, Width = 412, Height = 860 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var panel = view.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "SidePanel");
        var handle = view.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "SheetHandleButton");
        var content = view.GetVisualDescendants().OfType<ScrollViewer>().Single(scroll => scroll.Name == "SidePanelContent");
        using var touch = new HeadlessTouch(window);
        Assert.True(content.IsVisible);

        await SwipeHandle(touch, handle, 80);

        Assert.False(content.IsVisible);
        Assert.Equal(52, panel.Height);

        await SwipeHandle(touch, handle, -80);

        Assert.True(content.IsVisible);
        Assert.True(panel.Height > 52);
        window.Close();
    }

    private static async Task SwipeHandle(HeadlessTouch touch, Border handle, double distance)
    {
        var start = await touch.TargetAsync(handle);
        var end = start + new Point(0, distance);
        touch.Send(RawPointerEventType.TouchBegin, start);
        touch.Send(RawPointerEventType.TouchUpdate, end);
        touch.Send(RawPointerEventType.TouchEnd, end);
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
        using var provider = TestApplication.CreateServices(
            TestData.RealData,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"),
            new MemoryClipboard());
        Assert.Throws<ArgumentException>(
            () => provider.GetRequiredService<TypedViewLocator>().ResolveView(new object()));
    }
}
