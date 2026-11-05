using Avalonia;
using System.Reactive.Subjects;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Raw;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.UI.Views;
using CableRoutePlanner.ViewModels;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class LocationUiTests
{
    [AvaloniaFact]
    public void DesktopSettingsUseGenericTitleAndHideLocationSection()
    {
        using SettingsViewModel viewModel = new(new NavigationHost(), new MemorySettings());
        var view = new SettingsView { ViewModel = viewModel };
        var window = new Window { Content = view, Width = 600, Height = 800 };
        window.Show();

        TextBlock title = view.FindControl<TextBlock>("SettingsTitle")!;
        Control locationSection = view.FindControl<Control>("LocationSection")!;

        Assert.Equal("Настройки", title.Text);
        Assert.False(locationSection.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AndroidSettingsShowLocationSectionAndImmediateToggle()
    {
        using TrackingViewModel tracking = CreateTracking();
        using SettingsViewModel viewModel = new(new NavigationHost(), new MemorySettings(), tracking);
        var view = new SettingsView { ViewModel = viewModel };
        var window = new Window { Content = view, Width = 600, Height = 800 };
        window.Show();

        ToggleSwitch toggle = view.FindControl<ToggleSwitch>("LocationToggle")!;
        Control locationSection = view.FindControl<Control>("LocationSection")!;

        Assert.True(locationSection.IsEffectivelyVisible);
        Assert.True(toggle.IsEffectivelyVisible);
        Assert.Equal("Показывать моё местоположение", toggle.Content);

        toggle.IsChecked = true;
        await viewModel.ToggleLocation.Execute(Unit.Default).ToTask();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.ShowLocation);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AndroidMapShowsOnlyValidTraversalActionsAndCompactCollapsedProgress()
    {
        using TrackingViewModel tracking = CreateTracking();
        using MapViewModel viewModel = TestData.CreateMapViewModel(
            new NavigationHost(),
            new Application.PlanningSession(TestData.Planner(), TestData.AllowAllValidator),
            new MapAreaLoader(new MemoryData()),
            new MemorySettings(),
            new MemoryClipboard(),
            tracking);
        var view = new MapView((control, bounds) => new CableRoutePlanner.UI.MapAdapters.MapsuiMapAdapter(control, bounds))
        {
            ViewModel = viewModel
        };
        var window = new Window { Content = view, Width = 600, Height = 800 };
        window.Show();
        viewModel.SelectedRoute = new RouteAlternative(
            Guid.NewGuid(),
            "Маршрут",
            [TestData.P(0, 0), TestData.P(100, 0)],
            new RouteMetrics(100, 100, 0, 0, 0, 0, 100, null));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Button locationButton = view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "LocationButton");
        Assert.True(locationButton.IsEffectivelyVisible);
        Assert.True(view.FindControl<Control>("TrackingPanel")!.IsEffectivelyVisible);
        Button start = view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "StartTraversalButton");
        Button pause = view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PauseTraversalButton");
        Button resume = view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ResumeTraversalButton");
        Button finish = view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "FinishTraversalButton");
        Assert.True(start.IsEffectivelyVisible);
        Assert.False(pause.IsEffectivelyVisible);
        Assert.False(resume.IsEffectivelyVisible);
        Assert.False(finish.IsEffectivelyVisible);

        await tracking.Start.Execute(Unit.Default).ToTask();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(start.IsEffectivelyVisible);
        Assert.True(pause.IsEffectivelyVisible);
        Assert.False(resume.IsEffectivelyVisible);
        Assert.True(finish.IsEffectivelyVisible);

        Border handle = view.FindControl<Border>("SheetHandleButton")!;
        using HeadlessTouch touch = new(window);
        Point startPoint = await touch.TargetAsync(handle);
        Point endPoint = startPoint + new Point(0, 80);
        touch.Send(Avalonia.Input.Raw.RawPointerEventType.TouchBegin, startPoint);
        touch.Send(Avalonia.Input.Raw.RawPointerEventType.TouchUpdate, endPoint);
        touch.Send(Avalonia.Input.Raw.RawPointerEventType.TouchEnd, endPoint);
        Assert.Contains("Обход:", view.FindControl<TextBlock>("SheetHandleText")!.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void DesktopMapHidesTraversalPanelEvenWhenRouteIsSelected()
    {
        using MapViewModel viewModel = TestData.CreateMapViewModel(
            new NavigationHost(),
            new Application.PlanningSession(TestData.Planner(), TestData.AllowAllValidator),
            new MapAreaLoader(new MemoryData()),
            new MemorySettings(),
            new MemoryClipboard());
        var view = new MapView((control, bounds) => new CableRoutePlanner.UI.MapAdapters.MapsuiMapAdapter(control, bounds))
        {
            ViewModel = viewModel
        };
        var window = new Window { Content = view, Width = 600, Height = 800 };
        window.Show();
        viewModel.SelectedRoute = new RouteAlternative(
            Guid.NewGuid(),
            "Маршрут",
            [TestData.P(0, 0), TestData.P(100, 0)],
            new RouteMetrics(100, 100, 0, 0, 0, 0, 100, null));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.False(view.FindControl<Control>("TrackingPanel")!.IsEffectivelyVisible);
        window.Close();
    }

    private static TrackingViewModel CreateTracking() => new(
        new UiLocationService(),
        new UiHeadingService(),
        new UiLocationPreferences(),
        new UiTrackingSessions());

    private sealed class UiLocationService : ILocationService
    {
        public bool IsSupported => true;
        public LocationServiceState State { get; private set; } = LocationServiceState.Disabled;
        public IObservable<LocationMeasurement> Measurements { get; } = new Subject<LocationMeasurement>();
        public IObservable<LocationServiceState> States { get; } = new Subject<LocationServiceState>();
        public Task<LocationServiceState> EnableAsync(CancellationToken cancellationToken)
        {
            State = LocationServiceState.Enabled;
            return Task.FromResult(State);
        }

        public Task<LocationServiceState> RestoreAsync(CancellationToken cancellationToken) =>
            Task.FromResult(State);
        public void Disable() => State = LocationServiceState.Disabled;
        public void PauseUpdates() { }
        public void ResumeUpdates() { }
        public void OpenSystemSettings() { }
        public void Dispose() { }
    }

    private sealed class UiHeadingService : IHeadingService
    {
        public bool IsSupported => false;
        public IObservable<HeadingMeasurement> Measurements { get; } = new Subject<HeadingMeasurement>();
        public void PauseUpdates() { }
        public void ResumeUpdates() { }
        public void Dispose() { }
    }

    private sealed class UiLocationPreferences : ILocationPreferencesStore
    {
        public LocationPreferences Load() => new();
        public void Save(LocationPreferences preferences) { }
    }

    private sealed class UiTrackingSessions : ITrackingSessionStore
    {
        public RouteProgressSnapshot? Load() => null;
        public void Save(RouteProgressSnapshot snapshot) { }
        public void Clear() { }
    }
}
