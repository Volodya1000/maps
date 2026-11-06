using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
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
        Assert.NotNull(view.FindControl<ScrollViewer>("SidePanelContent"));
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
