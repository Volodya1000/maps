using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using System.Reactive.Threading.Tasks;
using CableRoutePlanner.Application;
using CableRoutePlanner.UI.MapAdapters;
using CableRoutePlanner.UI.Views;
using CableRoutePlanner.ViewModels;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class ResponsiveLayoutTests
{
    [AvaloniaFact]
    public void DesktopMapUsesLogicalToolTabsInsteadOfOneGlobalToolScroll()
    {
        using MapViewModel viewModel = CreateViewModel();
        MapView view = CreateView(viewModel);
        Window window = new() { Content = view, Width = 1280, Height = 850 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        TabControl tabs = Assert.Single(view.GetVisualDescendants().OfType<TabControl>());
        Assert.Equal(3, tabs.Items.Count);
        Assert.NotNull(view.FindControl<ScrollViewer>("WaypointListScroll"));
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 850)]
    [InlineData(1024, 768)]
    public async Task DesktopPointListScrollsWithoutHidingRouteBuildAction(double width, double height)
    {
        using MapViewModel viewModel = CreateViewModel();
        MapView view = CreateView(viewModel);
        Window window = new() { Content = view, Width = width, Height = height };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        for (int index = 0; index < 40; index++)
        {
            await viewModel.AddPoint.Execute(TestData.P(30 + index, 150)).ToTask();
        }

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        ScrollViewer list = view.FindControl<ScrollViewer>("WaypointListScroll")!;
        Button build = view.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "Построить маршруты"));
        Avalonia.Point listBottom = list.TranslatePoint(new Avalonia.Point(0, list.Bounds.Height), window)!.Value;
        Avalonia.Point buildTop = build.TranslatePoint(new Avalonia.Point(0, 0), window)!.Value;

        Assert.True(list.IsEffectivelyVisible);
        Assert.True(build.IsEffectivelyVisible);
        Assert.True(buildTop.Y >= listBottom.Y);
        Assert.True(list.Extent.Height > list.Viewport.Height);
        window.Close();
    }

    [AvaloniaFact]
    public void NarrowMapStartsWithCollapsedBottomSheet()
    {
        using MapViewModel viewModel = CreateViewModel();
        MapView view = CreateView(viewModel);
        Window window = new() { Content = view, Width = 420, Height = 620 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Border handle = view.FindControl<Border>("SheetHandleButton")!;
        TabControl content = view.FindControl<TabControl>("ToolTabs")!;
        Assert.True(handle.IsEffectivelyVisible);
        Assert.False(content.IsEffectivelyVisible);
        window.Close();
    }

    private static MapViewModel CreateViewModel() => TestData.CreateMapViewModel(
        new NavigationHost(),
        new PlanningSession(TestData.Planner(), TestData.AllowAllValidator),
        new MapAreaLoader(new MemoryData()),
        new MemorySettings(),
        new MemoryClipboard());

    private static MapView CreateView(MapViewModel viewModel) => new(
        (control, bounds) => new MapsuiMapAdapter(control, bounds))
    {
        ViewModel = viewModel
    };
}
