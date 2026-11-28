using System.Reactive.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.UI;
using CableRoutePlanner.UI.Views;
using CableRoutePlanner.ViewModels;
using Mapsui;
using Mapsui.Projections;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class SavedRoutesUiTests
{
    [AvaloniaTheory]
    [InlineData(360, 640)]
    [InlineData(420, 620)]
    [InlineData(1280, 850)]
    public async Task SavedNavigationAndRowActionsRemainAccessibleAndOpeningFitsRoute(double width, double height)
    {
        using SavedRouteDirectory directory = new();
        using ServiceProvider provider = TestApplication.CreateServices(TestData.RealData,
            Path.Combine(directory.DirectoryPath, "settings.json"), new MemoryClipboard());
        ISavedRouteStore store = provider.GetRequiredService<ISavedRouteStore>();
        // Use actual package coordinates, far from the previous camera.
        GeoCoordinate a = new(53.9409, 27.4977);
        GeoCoordinate b = new(53.9424, 27.4991);
        RouteAlternative route = new(Guid.NewGuid(), "Единственный", [a, b], new(190, 0, 190, 0, 0, 1, 190, null));
        SavedRoute snapshot = new(Guid.NewGuid(), "", DateTimeOffset.UtcNow,
            [new(Guid.NewGuid(), a), new(Guid.NewGuid(), b)], [route], route.Id, new());
        store.Add(snapshot);
        ShellView shell = provider.GetRequiredService<ShellView>();
        ShellViewModel vm = shell.ViewModel!;
        Window window = new() { Content = shell, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        MapViewModel map = provider.GetRequiredService<MapViewModel>();
        map.UpdateViewportState(new(3_000_000, 7_000_000, 20, 0));
        ToggleButtonCheck(shell, window);
        await vm.ShowSaved.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        SavedRoutesView savedView = Assert.Single(shell.GetVisualDescendants().OfType<SavedRoutesView>());
        Button open = Assert.Single(savedView.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Открыть"));
        Button delete = Assert.Single(savedView.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Удалить"));
        AssertVisibleInside(open, window);
        AssertVisibleInside(delete, window);
        await provider.GetRequiredService<SavedRoutesViewModel>().Open.Execute(snapshot.Id).ToTask();
        Dispatcher.UIThread.RunJobs();
        MapView mapView = Assert.Single(shell.GetVisualDescendants().OfType<MapView>());
        Assert.Equal(1, mapView.FindControl<TabControl>("ToolTabs")!.SelectedIndex);
        Assert.True(mapView.FindControl<TabControl>("ToolTabs")!.IsEffectivelyVisible);
        Button save = mapView.FindControl<Button>("SaveRouteButton")!;
        AssertVisibleInside(save, window);
        Assert.True(save.IsEnabled);
        using Mapsui.UI.Avalonia.MapControl control = mapView.FindControl<Mapsui.UI.Avalonia.MapControl>("MapControl")!;
        Viewport viewport = control.Map.Navigator.Viewport;
        foreach (GeoCoordinate coordinate in route.Geometry)
        {
            (double x, double y) = SphericalMercator.FromLonLat(coordinate.Longitude, coordinate.Latitude);
            // Rotation is zero; both endpoints must be inside the usable map.
            Assert.InRange(x, viewport.CenterX - viewport.Width * viewport.Resolution / 2,
                viewport.CenterX + viewport.Width * viewport.Resolution / 2);
            Assert.InRange(y, viewport.CenterY - viewport.Height * viewport.Resolution / 2,
                viewport.CenterY + viewport.Height * viewport.Resolution / 2);
        }
        if (width < 760)
        {
            Border panel = mapView.FindControl<Border>("SidePanel")!;
            double panelTop = panel.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            foreach (GeoCoordinate coordinate in route.Geometry)
            {
                (double x, double y) = SphericalMercator.FromLonLat(coordinate.Longitude, coordinate.Latitude);
                double screenY = viewport.Height / 2 - (y - viewport.CenterY) / viewport.Resolution;
                Point endpoint = control.TranslatePoint(new Point(viewport.Width / 2 + (x - viewport.CenterX) / viewport.Resolution, screenY), window)!.Value;
                Assert.True(endpoint.Y < panelTop, $"Route endpoint at {endpoint.Y} is covered by panel at {panelTop}.");
            }
        }
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("маршрут номер 1")]
    [InlineData("маршрут номер 2")]
    public async Task TypedRouteNameIsPersistedAndDisplayedAfterTouchSaveAndReload(string name)
    {
        using SavedRouteDirectory directory = new();
        SavedRoute snapshot = SavedRouteDirectory.Snapshot();
        directory.Store.Add(snapshot);
        NavigationHost screen = new();
        using MapViewModel map = SavedRoutesTests.CreateMap(directory, new SavedRoutesTests.RejectPlanner(), screen);
        Assert.True(map.OpenSavedRoute(snapshot.Id));
        MapView view = new((control, bounds) => new CableRoutePlanner.UI.MapAdapters.MapsuiMapAdapter(control, bounds))
        {
            ViewModel = map
        };
        using Mapsui.UI.Avalonia.MapControl mapControl = view.FindControl<Mapsui.UI.Avalonia.MapControl>("MapControl")!;
        Window window = new() { Content = view, Width = 360, Height = 640 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        using HeadlessTouch touch = new(window);
        Button begin = view.FindControl<Button>("SaveRouteButton")!;
        Point beginPoint = await touch.TargetAsync(begin);
        touch.Send(RawPointerEventType.TouchBegin, beginPoint);
        touch.Send(RawPointerEventType.TouchEnd, beginPoint);
        TextBox input = Assert.Single(view.GetVisualDescendants().OfType<TextBox>(),
            box => Equals(box.Watermark, "Название (необязательно)"));
        Point inputPoint = await touch.TargetAsync(input);
        touch.Send(RawPointerEventType.TouchBegin, inputPoint);
        touch.Send(RawPointerEventType.TouchEnd, inputPoint);
        window.KeyTextInput(name);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(name, input.Text);
        Assert.Same(map, input.DataContext);
        Assert.Equal(name, map.RouteName);
        Button save = Assert.Single(view.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Сохранить"));
        Point savePoint = await touch.TargetAsync(save);
        touch.Send(RawPointerEventType.TouchBegin, savePoint);
        touch.Send(RawPointerEventType.TouchEnd, savePoint);
        SavedRoute saved = Assert.Single(directory.Store.List(), record => record.Id != snapshot.Id);
        Assert.Equal(name, saved.Name);
        Assert.Equal("Маршрут сохранён в «Сохранённых».", map.SaveStatus);
        using SavedRoutesViewModel list = new(screen, map, directory.Store);
        list.Reload();
        SavedRoutesView savedView = new() { ViewModel = list };
        window.Content = savedView;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(savedView.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == name);
        window.Close();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("маршрут номер 1")]
    public void SavedRowDisplaysTheAddedDateExactlyOnce(string? name)
    {
        SavedRouteRow row = new(SavedRouteDirectory.Snapshot() with { Name = name });
        string display = row.Title + " " + row.Details;
        Assert.Equal(1, display.Split(row.AddedText, StringSplitOptions.None).Length - 1);
        if (!string.IsNullOrWhiteSpace(name))
        {
            Assert.Equal(name, row.Title);
        }
    }

    [AvaloniaFact]
    public async Task MapAndSettingsHaveNoCreditWhileMapNoticesRemainAvailable()
    {
        using MapViewModel map = TestData.CreateMapViewModel(new NavigationHost(),
            new PlanningSession(new SavedRoutesTests.RejectPlanner(), TestData.AllowAllValidator),
            new MapAreaLoader(new MemoryData()), new MemorySettings(), new MemoryClipboard());
        MapOverlayControlsView overlay = new() { ViewModel = map };
        Window window = new() { Content = overlay, Width = 360, Height = 640 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(overlay.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text?.Contains("OpenStreetMap", StringComparison.Ordinal) == true
                || block.Text?.Contains("ODbL", StringComparison.Ordinal) == true);
        Assert.Empty(map.MapStatus);
        Assert.False(overlay.FindControl<Border>("MapNotices")!.IsEffectivelyVisible);
        Assert.DoesNotContain(overlay.GetVisualDescendants().OfType<TextBlock>(),
            block => block.IsEffectivelyVisible && (block.Text == "ⓘ" || block.Text == "Загрузка офлайн-данных…"));
        // Real notices still appear; only the invented initial loading state is removed.
        await map.LoadMapAreaAsync(map.DataInfo.Bounds, CancellationToken.None);
        Assert.True(overlay.FindControl<Border>("MapNotices")!.IsEffectivelyVisible);
        Assert.Contains(overlay.GetVisualDescendants().OfType<TextBlock>(),
            block => block.IsEffectivelyVisible && block.Text == "Для этой области нет данных в офлайн-пакете.");
        using SettingsViewModel settings = new(new NavigationHost(), new MemorySettings());
        SettingsView settingsView = new() { ViewModel = settings };
        window.Content = settingsView;
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(settingsView.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text == "© OpenStreetMap contributors · ODbL 1.0");
        window.Close();
    }

    private static void ToggleButtonCheck(ShellView shell, Window window)
    {
        foreach (string title in new[] { "Карта", "Сохранённые", "Настройки" })
        {
            Avalonia.Controls.Primitives.ToggleButton button = Assert.Single(
                shell.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>(),
                item => Equals(item.Content, title));
            AssertVisibleInside(button, window);
        }
    }

    private static void AssertVisibleInside(Control control, Window window)
    {
        Assert.True(control.IsEffectivelyVisible);
        Point top = control.TranslatePoint(new Point(0, 0), window)!.Value;
        Assert.InRange(top.X, 0, window.Bounds.Width - control.Bounds.Width);
        Assert.InRange(top.Y, 0, window.Bounds.Height - control.Bounds.Height);
    }
}
