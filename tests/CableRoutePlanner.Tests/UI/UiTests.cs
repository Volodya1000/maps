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
using CableRoutePlanner.Domain;
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
        using MapViewModel vm = TestData.CreateMapViewModel(new NavigationHost(),
            new CableRoutePlanner.Application.PlanningSession(TestData.Planner(), TestData.AllowAllValidator),
            new MapAreaLoader(new MemoryData()), new MemorySettings(), new MemoryClipboard());
        await vm.AddPoint.Execute(TestData.P(30, 150));
        await vm.AddPoint.Execute(TestData.P(300, 150));
        var view = new MapView((control, bounds) => new CableRoutePlanner.UI.MapAdapters.MapsuiMapAdapter(control, bounds))
        {
            ViewModel = vm
        };
        var window = new Window { Content = view, Width = 1100, Height = 900 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Button compass = view.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Name == "NorthButton");
        Assert.Equal(46d, compass.Width);
        Assert.Equal(46d, compass.Height);
        Assert.Equal(Avalonia.Media.Colors.Transparent,
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(compass.Background).Color);
        Border face = view.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Classes.Contains("compass-face", StringComparer.Ordinal));
        Assert.Equal(new CornerRadius(23), face.CornerRadius);
        Assert.Equal(Avalonia.Media.Color.Parse("#F9FFFFFF"),
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(face.Background).Color);

        Border[] waypoints = view.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Name == "CoordinateCard").ToArray();
        Assert.Equal(2, waypoints.Length);
        Border selected = Assert.Single(waypoints,
            border => border.DataContext is WaypointRow { IsSelected: true });
        Border unselected = Assert.Single(waypoints,
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
    public async Task MapAdapterRendersEveryTerrainCategory()
    {
        // Arrange
        var control = new Mapsui.UI.Avalonia.MapControl();
        using var adapter = new CableRoutePlanner.UI.MapAdapters.MapsuiMapAdapter(
            control,
            new CableRoutePlanner.Domain.GeoBounds(27, 53, 28, 54));
        Domain.TerrainKind[] kinds = Enum.GetValues<CableRoutePlanner.Domain.TerrainKind>();
        MapFeature[] features = kinds.Select((kind, index) => TestData.Polygon(
            index + 1,
            kind,
            10 + index * 3,
            10,
            12 + index * 3,
            12)).ToArray();

        // Act
        await adapter.SetFeaturesAsync(features);

        // Assert
        Mapsui.Layers.MemoryLayer rendered = Assert.Single(control.Map.Layers.OfType<Mapsui.Layers.MemoryLayer>(),
            layer => layer.Name == "Офлайн OSM");
        Assert.Equal(kinds.Length, rendered.Features!.Count());
        Assert.All(rendered.Features, feature => Assert.NotEmpty(feature.Styles));
    }

    [AvaloniaFact]
    public async Task ProductionMapAdapterRasterizesRepositoryDataByTile()
    {
        // Arrange
        MapFeature road = TestData.Line(1, Domain.TerrainKind.Road, (10, 10), (200, 200));
        MapFeature bridge = TestData.Line(2, Domain.TerrainKind.BridgeCandidate, (20, 10), (20, 200));
        var repository = new TileRepository(road, bridge);
        var control = new Mapsui.UI.Avalonia.MapControl();
        using var adapter = new CableRoutePlanner.UI.MapAdapters.MapsuiMapAdapter(
            control,
            repository.Info.Bounds,
            repository);
        Mapsui.Tiling.Layers.RasterizingTileLayer tiled = Assert.Single(
            control.Map.Layers.OfType<Mapsui.Tiling.Layers.RasterizingTileLayer>());
        Mapsui.Layers.Layer source = Assert.IsType<Mapsui.Layers.Layer>(tiled.SourceLayer);
        Mapsui.Providers.IProvider provider = Assert.IsAssignableFrom<Mapsui.Providers.IProvider>(source.DataSource);
        (double west, double south) = Mapsui.Projections.SphericalMercator.FromLonLat(27, 54);
        (double east, double north) = Mapsui.Projections.SphericalMercator.FromLonLat(27.006, 54.006);
        var extent = new Mapsui.MRect(west, south, east, north);

        // Act
        IEnumerable<Mapsui.IFeature> features = await provider.GetFeaturesAsync(
            new Mapsui.Layers.FetchInfo(new Mapsui.MSection(extent, 4), "EPSG:3857"));
        await adapter.SetFeaturesAsync([TestData.Polygon(2, Domain.TerrainKind.Forest, 0, 0, 5, 5)]);

        // Assert
        Assert.Equal(1, repository.MapQueries);
        Assert.Equal(4, repository.Resolution);
        Assert.NotNull(repository.LastBounds);
        Assert.Equal(27, repository.LastBounds.Value.West, 4);
        Assert.Equal(54, repository.LastBounds.Value.South, 4);
        Mapsui.IFeature[] rendered = features.ToArray();
        Assert.Equal(2, rendered.Length);
        Mapsui.IFeature renderedRoad = rendered[0];
        Mapsui.Styles.VectorStyle roadStyle = Assert.IsType<Mapsui.Styles.VectorStyle>(
            Assert.Single(renderedRoad.Styles));
        Assert.NotNull(roadStyle.Line);
        Assert.InRange(roadStyle.Line.Width, 0.5, 1.1);
        Mapsui.IFeature renderedBridge = rendered[1];
        Assert.Equal(2, renderedBridge.Styles.Count);
        Mapsui.Styles.VectorStyle bridgeMask = Assert.IsType<Mapsui.Styles.VectorStyle>(
            renderedBridge.Styles.First());
        Mapsui.Styles.Pen bridgeMaskLine = Assert.IsType<Mapsui.Styles.Pen>(bridgeMask.Line);
        Assert.Equal(new Mapsui.Styles.Color(244, 242, 233), bridgeMaskLine.Color);
        Assert.Equal(Mapsui.Styles.PenStrokeCap.Square, bridgeMaskLine.PenStrokeCap);
        Assert.DoesNotContain(
            control.Map.Layers.OfType<Mapsui.Layers.MemoryLayer>(),
            layer => layer.Name == "Офлайн OSM");
    }

    [AvaloniaFact]
    public async Task MapAdapterKeepsPreviousLayerWhenNewGeometryCannotBeRead()
    {
        var control = new Mapsui.UI.Avalonia.MapControl();
        using var adapter = new CableRoutePlanner.UI.MapAdapters.MapsuiMapAdapter(control, new(27, 53, 28, 54));
        MapFeature original = TestData.Polygon(1, Domain.TerrainKind.Field, 10, 10, 12, 12);
        await adapter.SetFeaturesAsync([original]);
        Mapsui.Layers.MemoryLayer layer = Assert.Single(control.Map.Layers.OfType<Mapsui.Layers.MemoryLayer>(),
            candidate => candidate.Name == "Офлайн OSM");
        Mapsui.IFeature displayed = Assert.Single(layer.Features);

        await Assert.ThrowsAnyAsync<Exception>(() => adapter.SetFeaturesAsync(
            [new MapFeature(2, Domain.TerrainKind.Road, [], "invalid")]));

        Assert.Same(displayed, Assert.Single(layer.Features));
    }

    [AvaloniaFact]
    public async Task MapAdapterPublishesLatestFeaturesAfterOverlappingPreparations()
    {
        var control = new Mapsui.UI.Avalonia.MapControl();
        using var adapter = new CableRoutePlanner.UI.MapAdapters.MapsuiMapAdapter(control, new(27, 53, 28, 54));
        MapFeature[] oldFeatures = Enumerable.Range(0, 200).Select(index => TestData.Polygon(
            index + 1, Domain.TerrainKind.Field, 10 + index * 0.01, 10,
            10.005 + index * 0.01, 10.005)).ToArray();
        MapFeature newest = TestData.Polygon(300, Domain.TerrainKind.Settlement, 15, 15, 16, 16);

        Task oldPreparation = adapter.SetFeaturesAsync(oldFeatures);
        Task newPreparation = adapter.SetFeaturesAsync([newest]);
        await Task.WhenAll(oldPreparation, newPreparation);

        Mapsui.Layers.MemoryLayer layer = Assert.Single(control.Map.Layers.OfType<Mapsui.Layers.MemoryLayer>(),
            candidate => candidate.Name == "Офлайн OSM");
        Mapsui.IFeature displayed = Assert.Single(layer.Features);
        Assert.Single(displayed.Styles.OfType<Mapsui.Styles.LabelStyle>());
    }

    [AvaloniaFact]
    public async Task DisposingMapAdapterCancelsPendingFeaturePreparation()
    {
        var control = new Mapsui.UI.Avalonia.MapControl();
        var adapter = new CableRoutePlanner.UI.MapAdapters.MapsuiMapAdapter(control, new(27, 53, 28, 54));
        MapFeature[] features = Enumerable.Range(0, 200).Select(index => TestData.Polygon(
            index + 1, Domain.TerrainKind.Field, 10 + index * 0.01, 10,
            10.005 + index * 0.01, 10.005)).ToArray();

        Task preparation = adapter.SetFeaturesAsync(features);
        adapter.Dispose();

        await preparation;
        Assert.True(preparation.IsCompletedSuccessfully);
    }

    [AvaloniaFact]
    public async Task SystemBackAndWindowCloseWarnAboutUnsavedSettings()
    {
        using ServiceProvider provider = TestApplication.CreateServices(TestData.RealData,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), new MemoryClipboard());
        ShellView shell = provider.GetRequiredService<ShellView>();
        ShellViewModel vm = shell.ViewModel!;
        SettingsViewModel settings = provider.GetRequiredService<SettingsViewModel>();
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
        NumericUpDown[] inputs = view.GetVisualDescendants().OfType<NumericUpDown>().ToArray();
        Assert.Equal(20m, inputs[0].Value);
        Assert.Equal(10m, inputs[1].Value);
        Assert.NotEmpty(inputs[0].GetVisualDescendants().OfType<TextBox>().Single().Text!);
        Assert.NotEmpty(inputs[1].GetVisualDescendants().OfType<TextBox>().Single().Text!);
        Button save = view.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Сохранить"));
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
        string settingsPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        using ServiceProvider provider = TestApplication.CreateServices(TestData.RealData, settingsPath, new MemoryClipboard());
        ShellView shell = provider.GetRequiredService<ShellView>();
        SettingsViewModel settings = provider.GetRequiredService<SettingsViewModel>();
        var window = new Window { Content = shell, Width = 900, Height = 700 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        await shell.ViewModel!.ShowSettings.Execute();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        NumericUpDown[] inputs = shell.GetVisualDescendants().OfType<NumericUpDown>().ToArray();
        decimal? cellSize = inputs[0].Value;
        decimal? reservePercent = inputs[1].Value;
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
        using MapViewModel vm = TestData.CreateMapViewModel(new NavigationHost(),
            new CableRoutePlanner.Application.PlanningSession(TestData.Planner(), TestData.AllowAllValidator),
            new MapAreaLoader(new MemoryData()), new MemorySettings(), new MemoryClipboard());
        var view = new MapView((control, bounds) => new CableRoutePlanner.UI.MapAdapters.MapsuiMapAdapter(control, bounds))
        {
            ViewModel = vm
        };
        var window = new Window { Content = view, Width = 1100, Height = 900 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        ItemsControl choices = view.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "RouteChoices");
        StackPanel section = choices.GetVisualAncestors().OfType<StackPanel>().First();
        Assert.False(section.IsVisible);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "3D"));
        TextBlock attribution = view.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Name == "AttributionCredit");
        Assert.Contains("OpenStreetMap contributors", attribution.GetValue(Avalonia.Automation.AutomationProperties.NameProperty)!.ToString());
        await vm.AddPoint.Execute(TestData.P(30, 150));
        await vm.AddPoint.Execute(TestData.P(300, 150));
        Assert.False(section.IsVisible);
        await vm.BuildRoutes.Execute();
        TextBlock routeVertexHeader = view.GetVisualDescendants().OfType<Expander>()
            .Select(expander => expander.Header)
            .OfType<TextBlock>()
            .Single(item => item.Text == "Точки прокладки кабеля");
        Assert.Equal("Точки прокладки кабеля", routeVertexHeader.Text);
        Assert.True(section.IsVisible);
        Assert.False(choices.IsVisible);
        Assert.Equal("Маршрут", Assert.Single(vm.RouteChoices).Name);
        await vm.ClearPoints.Execute();
        Assert.False(section.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void RotatingCompassKeepsTheNeedleCenteredInsideTheFixedButton()
    {
        using MapViewModel vm = TestData.CreateMapViewModel(new NavigationHost(),
            new CableRoutePlanner.Application.PlanningSession(TestData.Planner(), TestData.AllowAllValidator),
            new MapAreaLoader(new MemoryData()), new MemorySettings(), new MemoryClipboard());
        var view = new MapView((control, bounds) =>
            new CableRoutePlanner.UI.MapAdapters.MapsuiMapAdapter(control, bounds))
        {
            ViewModel = vm
        };
        var window = new Window { Content = view, Width = 412, Height = 860 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Button button = view.GetVisualDescendants().OfType<Button>()
            .Single(candidate => candidate.Name == "NorthButton");
        Canvas needle = view.GetVisualDescendants().OfType<Canvas>()
            .Single(candidate => candidate.Name == "NorthArrow");
        Mapsui.UI.Avalonia.MapControl map = view.GetVisualDescendants().OfType<Mapsui.UI.Avalonia.MapControl>().Single();
        Point buttonPosition = button.TranslatePoint(new Point(0, 0), window)!.Value;
        Point needleCenter = needle.TranslatePoint(new Point(23, 23), button)!.Value;
        Assert.Equal(46, needle.Bounds.Width);
        Assert.Equal(46, needle.Bounds.Height);

        foreach (double angle in new[] { 0d, 45d, 90d, 180d, 270d })
        {
            Mapsui.Viewport viewport = map.Map.Navigator.Viewport;
            map.Map.Navigator.SetViewport(
                new Mapsui.Viewport(viewport.CenterX, viewport.CenterY, viewport.Resolution,
                    angle, viewport.Width, viewport.Height),
                0, new Mapsui.Animations.Easing(progress => progress));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Point currentCenter = needle.TranslatePoint(new Point(23, 23), button)!.Value;
            Point currentButtonPosition = button.TranslatePoint(new Point(0, 0), window)!.Value;
            Assert.Equal(needleCenter.X, currentCenter.X, 2);
            Assert.Equal(needleCenter.Y, currentCenter.Y, 2);
            Assert.Equal(buttonPosition.X, currentButtonPosition.X, 2);
            Assert.Equal(buttonPosition.Y, currentButtonPosition.Y, 2);
        }

        window.Close();
    }

    [AvaloniaFact]
    public void CompiledXamlAndTypedViewLocatorResolveAllRegisteredScreens()
    {
        using ServiceProvider provider = TestApplication.CreateServices(
            TestData.RealData,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"),
            new MemoryClipboard());
        TypedViewLocator locator = provider.GetRequiredService<TypedViewLocator>();
        Assert.IsType<MapView>(locator.ResolveView(provider.GetRequiredService<MapViewModel>()));
        Assert.IsType<SettingsView>(locator.ResolveView(provider.GetRequiredService<SettingsViewModel>()));
        ShellView shell = provider.GetRequiredService<ShellView>();
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
        using ServiceProvider provider = TestApplication.CreateServices(
            TestData.RealData,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"),
            new MemoryClipboard());
        MapViewModel vm = provider.GetRequiredService<MapViewModel>();
        IPlanningSession session = provider.GetRequiredService<CableRoutePlanner.Application.IPlanningSession>();
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
        Domain.GeoCoordinate before = vm.Points[0].Coordinate;
        long revision = session.Revision;
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
        Stopwatch timeout = Stopwatch.StartNew();
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
        MapFeature forest = TestData.Polygon(1, CableRoutePlanner.Domain.TerrainKind.Forest, 100, 60, 220, 240);
        using MapViewModel vm = TestData.CreateMapViewModel(new NavigationHost(),
            new CableRoutePlanner.Application.PlanningSession(TestData.Planner(forest), TestData.AllowAllValidator),
            new MapAreaLoader(new MemoryData(forest)), new MemorySettings(), new MemoryClipboard());
        await vm.AddPoint.Execute(TestData.P(30, 150));
        await vm.AddPoint.Execute(TestData.P(300, 150));
        await vm.BuildRoutes.Execute();
        await vm.ChooseRoute.Execute(vm.Alternatives.First(route => route.Geometry.Count > 2).Id);
        var view = new MapView((control, bounds) => new CableRoutePlanner.UI.MapAdapters.MapsuiMapAdapter(control, bounds))
        {
            ViewModel = vm
        };
        var window = new Window { Content = view, Width = 1100, Height = 1100 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        RouteChoice target = vm.RouteChoices.First(choice => choice.Id != vm.SelectedRoute!.Id);
        Button button = view.GetVisualDescendants().OfType<Button>()
            .Single(candidate => candidate.DataContext is RouteChoice choice && choice.Id == target.Id);
        Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == target.ForestText);
        Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == target.FieldText);
        Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == target.BridgesText);
        Expander verticesPanel = view.GetVisualDescendants().OfType<Expander>()
            .Single(expander => expander.Header is TextBlock text && text.Text == "Точки прокладки кабеля");
        verticesPanel.IsExpanded = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Button toggle = view.GetVisualDescendants().OfType<Button>()
            .First(candidate => ReferenceEquals(candidate.Command, vm.ToggleRouteLeg) && candidate.IsVisible);
        Avalonia.Controls.Shapes.Path closedIcon = Assert.Single(toggle.GetVisualDescendants()
            .OfType<Avalonia.Controls.Shapes.Path>(), path => path.IsVisible);
        Assert.NotNull(closedIcon.Data);
        Assert.NotNull(closedIcon.Stroke);
        toggle.Command!.Execute(toggle.CommandParameter);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Button primaryRow = view.GetVisualDescendants().OfType<Button>()
            .First(candidate => ReferenceEquals(candidate.Command, vm.SelectRouteVertex)
                && candidate.DataContext is RouteVertexRow { IsChild: false });
        Button childRow = view.GetVisualDescendants().OfType<Button>()
            .First(candidate => ReferenceEquals(candidate.Command, vm.SelectRouteVertex)
                && candidate.DataContext is RouteVertexRow { IsChild: true });
        Assert.True(childRow.TranslatePoint(new Point(0, 0), window)!.Value.X
            > primaryRow.TranslatePoint(new Point(0, 0), window)!.Value.X);
        verticesPanel.IsExpanded = false;
        button.BringIntoView();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Point position = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        Mapsui.UI.Avalonia.MapControl map = view.GetVisualDescendants().OfType<Mapsui.UI.Avalonia.MapControl>().Single();
        var layer = (Mapsui.Layers.MemoryLayer)map.Map.Layers.Single(candidate => candidate.Name == "Маршруты");
        NetTopologySuite.Geometries.Geometry before = ((Mapsui.Nts.GeometryFeature)Assert.Single(layer.Features)).Geometry!.Copy();
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
        bool prepared = false;
        using ServiceProvider provider = TestApplication.CreateServices(TestData.RealData,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), new MemoryClipboard(),
            async (_, ct) => { await Task.Delay(10, ct); prepared = true; });
        StartupView view = provider.GetRequiredService<StartupView>();
        var window = new Window { Content = view, Width = 900, Height = 700 };
        window.Show();
        for (int attempt = 0; attempt < 100 && view.ViewModel!.Shell is null; attempt++)
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
        using MapViewModel vm = TestData.CreateMapViewModel(new NavigationHost(),
            new CableRoutePlanner.Application.PlanningSession(TestData.Planner(), TestData.AllowAllValidator),
            new MapAreaLoader(repository), new MemorySettings(), new MemoryClipboard());
        var view = new MapView((control, bounds) => new CableRoutePlanner.UI.MapAdapters.MapsuiMapAdapter(control, bounds))
        {
            ViewModel = vm
        };
        var window = new Window { Content = view, Width = 412, Height = 860 };
        window.Show();
        for (int attempt = 0; attempt < 100 && repository.LastBounds is null; attempt++)
        {
            await Task.Delay(20);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
        Assert.NotNull(repository.LastBounds);
        Domain.GeoBounds bounds = repository.LastBounds!.Value;
        Assert.True(bounds.East - bounds.West >= 9.5);
        Assert.True(repository.Resolution > 250);
        window.Close();
    }

    [AvaloniaFact]
    public async Task MobileMapPanelCanBeCollapsedAndSwipedOpenAgain()
    {
        using MapViewModel vm = TestData.CreateMapViewModel(new NavigationHost(),
            new CableRoutePlanner.Application.PlanningSession(TestData.Planner(), TestData.AllowAllValidator),
            new MapAreaLoader(new MemoryData()), new MemorySettings(), new MemoryClipboard());
        var view = new MapView((control, bounds) => new CableRoutePlanner.UI.MapAdapters.MapsuiMapAdapter(control, bounds))
        {
            ViewModel = vm
        };
        var window = new Window { Content = view, Width = 412, Height = 860 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Border panel = view.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "SidePanel");
        Border handle = view.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "SheetHandleButton");
        ScrollViewer content = view.GetVisualDescendants().OfType<ScrollViewer>().Single(scroll => scroll.Name == "SidePanelContent");
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
        Point start = await touch.TargetAsync(handle);
        Point end = start + new Point(0, distance);
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
        double provisional = map.Map.Navigator.Viewport.Resolution;
        window.Width = 412;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(map.Map.Navigator.Viewport.Resolution < provisional / 2);
        adapter.ZoomIn();
        double zoom = map.Map.Navigator.Viewport.Resolution;
        window.Width = 800;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(zoom, map.Map.Navigator.Viewport.Resolution);
        window.Close();
    }

    private sealed class ViewportRepository : CableRoutePlanner.Application.IGeodataRepository
    {
        public CableRoutePlanner.Application.DataPackageInfo Info { get; } = new(
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

    private sealed class TileRepository(params MapFeature[] features) : IGeodataRepository
    {
        public DataPackageInfo Info { get; } = new(
            "synthetic-tile-test",
            new(27, 54, 27.006, 54.006),
            "test",
            false,
            "test");

        public int MapQueries { get; private set; }

        public GeoBounds? LastBounds { get; private set; }

        public double Resolution { get; private set; }

        public Task<IReadOnlyList<MapFeature>> QueryAsync(GeoBounds bounds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MapFeature>>(features);

        public Task<IReadOnlyList<MapFeature>> QueryMapAsync(
            GeoBounds bounds,
            double metresPerPixel,
            CancellationToken ct)
        {
            MapQueries++;
            LastBounds = bounds;
            Resolution = metresPerPixel;
            return QueryAsync(bounds, ct);
        }
    }

    [AvaloniaFact]
    public void UnknownViewModelCannotFallBackToReflection()
    {
        using ServiceProvider provider = TestApplication.CreateServices(
            TestData.RealData,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"),
            new MemoryClipboard());
        Assert.Throws<ArgumentException>(
            () => provider.GetRequiredService<TypedViewLocator>().ResolveView(new object()));
    }
}
