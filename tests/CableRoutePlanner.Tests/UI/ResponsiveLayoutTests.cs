using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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

    [AvaloniaTheory]
    [InlineData(420, 620)]
    [InlineData(420, 440)]
    [InlineData(360, 480)]
    public async Task NarrowEditorScrollsExpandedManualInputAndKeepsBuildVisible(double width, double height)
    {
        using MapViewModel viewModel = CreateViewModel();
        MapView view = CreateView(viewModel);
        Window window = new() { Content = view, Width = width, Height = height };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        view.FindControl<Border>("SheetHandleButton")!.Focus();
        window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None);
        for (int index = 0; index < 40; index++)
        {
            await viewModel.AddPoint.Execute(TestData.P(30 + index, 150)).ToTask();
        }
        view.GetVisualDescendants().OfType<Expander>().First().IsExpanded = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        ScrollViewer editor = view.FindControl<ScrollViewer>("PointEditorScroll")!;
        Assert.NotNull(editor);
        Assert.True(editor.Viewport.Height > 40);
        Assert.True(editor.Extent.Height > editor.Viewport.Height);
        editor.Offset = new Vector(0, 100);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(editor.Offset.Y > 0);
        using HeadlessTouch touch = new(window);
        Point swipeStart = await touch.TargetAsync(editor);
        double beforeSwipe = editor.Offset.Y;
        touch.Send(Avalonia.Input.Raw.RawPointerEventType.TouchBegin, swipeStart);
        touch.Send(Avalonia.Input.Raw.RawPointerEventType.TouchUpdate, swipeStart - new Point(0, 60));
        touch.Send(Avalonia.Input.Raw.RawPointerEventType.TouchUpdate, swipeStart - new Point(0, 90));
        touch.Send(Avalonia.Input.Raw.RawPointerEventType.TouchEnd, swipeStart - new Point(0, 90));
        Assert.True(editor.Offset.Y > beforeSwipe);
        Button build = view.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "Построить маршруты"));
        Point bottom = build.TranslatePoint(new Point(0, build.Bounds.Height), window)!.Value;
        Assert.True(build.IsEffectivelyVisible);
        Assert.InRange(bottom.Y, 1, window.ClientSize.Height);
        TabItem[] tabs = view.GetVisualDescendants().OfType<TabItem>().ToArray();
        Assert.Equal(3, tabs.Length);
        Assert.All(tabs, tab => Assert.Equal(tabs[0].Bounds.Y, tab.Bounds.Y));
        window.Width = 1024;
        window.Height = 768;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, editor.VerticalScrollBarVisibility);
        window.Width = 420;
        window.Height = 620;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, editor.VerticalScrollBarVisibility);
        Assert.True(editor.Viewport.Height > 40);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SuccessfulBuildShowsRouteAndEditingPointsReturnsToEditor()
    {
        using MapViewModel viewModel = CreateViewModel();
        MapView view = CreateView(viewModel);
        Window window = new() { Content = view, Width = 420, Height = 620 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await viewModel.AddPoint.Execute(TestData.P(30, 150)).ToTask();
        await viewModel.AddPoint.Execute(TestData.P(300, 150)).ToTask();
        await viewModel.BuildRoutes.Execute().ToTask();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.HasRoute);
        Assert.Equal(1, view.FindControl<TabControl>("ToolTabs")!.SelectedIndex);
        await viewModel.AddPoint.Execute(TestData.P(350, 150)).ToTask();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.HasRoute);
        Assert.Equal(0, view.FindControl<TabControl>("ToolTabs")!.SelectedIndex);
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
