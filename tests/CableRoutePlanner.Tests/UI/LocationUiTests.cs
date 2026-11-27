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
        using LocationController controller = CreateController();
        using SettingsViewModel viewModel = new(new NavigationHost(), new MemorySettings(), controller);
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
        UiLocationService location = new();
        using LocationController controller = new(location, new UiHeadingService(), new UiLocationPreferences());
        using TrackingViewModel tracking = new(controller, new UiTrackingSessions());
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
        using Mapsui.UI.Avalonia.MapControl mapControlLifetime = view.FindControl<Mapsui.UI.Avalonia.MapControl>("MapControl")!;
        var window = new Window { Content = view, Width = 600, Height = 800 };
        window.Show();
        viewModel.SelectedRoute = new RouteAlternative(
            Guid.NewGuid(),
            "Маршрут",
            [TestData.P(0, 0), TestData.P(45, 0)],
            new RouteMetrics(45, 45, 0, 0, 0, 0, 45, null));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        view.FindControl<TabControl>("ToolTabs")!.SelectedIndex = 2;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Border handle = view.FindControl<Border>("SheetHandleButton")!;
        using HeadlessTouch touch = new(window);
        Point handlePoint = await touch.TargetAsync(handle);
        Point expandedPoint = handlePoint + new Point(0, -80);
        touch.Send(RawPointerEventType.TouchBegin, handlePoint);
        touch.Send(RawPointerEventType.TouchUpdate, expandedPoint);
        touch.Send(RawPointerEventType.TouchEnd, expandedPoint);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Button locationButton = view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "LocationButton");
        Assert.True(locationButton.IsEffectivelyVisible);
        Assert.True(view.FindControl<TabItem>("TraversalTab")!.IsVisible);
        Assert.True(view.FindControl<Control>("TrackingPanel")!.IsEffectivelyVisible);
        Button start = view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "StartTraversalButton");
        Button pause = view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PauseTraversalButton");
        Button resume = view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ResumeTraversalButton");
        Button finish = view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "FinishTraversalButton");
        Assert.True(start.IsEffectivelyVisible);
        Assert.False(pause.IsEffectivelyVisible);
        Assert.False(resume.IsEffectivelyVisible);
        Assert.False(finish.IsEffectivelyVisible);

        Assert.Equal("Начать прокладку кабеля", start.Content);
        Point disabledButtonPoint = await touch.TargetAsync(start);
        touch.Send(RawPointerEventType.TouchBegin, disabledButtonPoint);
        touch.Send(RawPointerEventType.TouchEnd, disabledButtonPoint);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        TextBlock locationRequirement = view.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Name == "LocationStartRequirement");
        Button openSettings = view.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Name == "OpenLocationSettingsButton");
        Assert.True(locationRequirement.IsEffectivelyVisible);
        Assert.Equal("Для отслеживания прокладки кабеля необходимо включить геолокацию", locationRequirement.Text);
        Assert.True(openSettings.IsEffectivelyVisible);
        Assert.False(start.IsEffectivelyVisible);
        Button cancel = view.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Name == "CancelLocationStartButton");
        Point cancelPoint = await touch.TargetAsync(cancel);
        touch.Send(RawPointerEventType.TouchBegin, cancelPoint);
        touch.Send(RawPointerEventType.TouchEnd, cancelPoint);
        Assert.True(start.IsEffectivelyVisible);
        Assert.False(openSettings.IsEffectivelyVisible);
        disabledButtonPoint = await touch.TargetAsync(start);
        touch.Send(RawPointerEventType.TouchBegin, disabledButtonPoint);
        touch.Send(RawPointerEventType.TouchEnd, disabledButtonPoint);
        Assert.False(start.IsEffectivelyVisible);

        await tracking.SetLocationEnabledAsync(true);
        Point buttonPoint = await touch.TargetAsync(start);
        touch.Send(RawPointerEventType.TouchBegin, buttonPoint);
        touch.Send(RawPointerEventType.TouchEnd, buttonPoint);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(tracking.IsTraversalActive);
        Assert.False(start.IsEffectivelyVisible);
        Assert.True(pause.IsEffectivelyVisible);
        Assert.False(resume.IsEffectivelyVisible);
        Assert.True(finish.IsEffectivelyVisible);

        DateTimeOffset started = DateTimeOffset.UtcNow.AddMinutes(-1);
        for (int metre = 0; metre <= 45; metre++)
        {
            location.Emit(new LocationMeasurement(TestData.P(metre, 0), 1, started.AddSeconds(metre)));
        }
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(100, tracking.Progress!.CoveragePercent, 2);
        Mapsui.UI.Avalonia.MapControl map = view.GetVisualDescendants().OfType<Mapsui.UI.Avalonia.MapControl>().Single();
        Assert.NotEmpty(map.Map.Layers.OfType<Mapsui.Layers.MemoryLayer>()
            .Single(layer => layer.Name == "Пройденный маршрут").Features);

        Point startPoint = await touch.TargetAsync(handle);
        Point endPoint = startPoint + new Point(0, 80);
        touch.Send(Avalonia.Input.Raw.RawPointerEventType.TouchBegin, startPoint);
        touch.Send(Avalonia.Input.Raw.RawPointerEventType.TouchUpdate, endPoint);
        touch.Send(Avalonia.Input.Raw.RawPointerEventType.TouchEnd, endPoint);
        Assert.Contains("Прокладка:", view.FindControl<TextBlock>("SheetHandleText")!.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task GpsRequirementReplacesResumeUntilCancelledOrLocationEnabled()
    {
        using LocationController controller = CreateController();
        using TrackingViewModel tracking = new(controller, new UiTrackingSessions());
        tracking.SetRoute(new(Guid.NewGuid(), "Маршрут", [TestData.P(0, 0), TestData.P(100, 0)],
            new(100, 0, 100, 0, 0, 0, 100, null)));
        await tracking.SetLocationEnabledAsync(true);
        await tracking.Start.Execute().ToTask();
        await tracking.Pause.Execute().ToTask();
        await tracking.SetLocationEnabledAsync(false);
        TrackingPanelView view = new() { ViewModel = tracking };
        Window window = new() { Content = view, Width = 360, Height = 640 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Button resume = view.FindControl<Button>("ResumeTraversalButton")!;
        Assert.True(resume.IsEffectivelyVisible);
        await tracking.Resume.Execute().ToTask();
        Assert.True(view.FindControl<TextBlock>("LocationStartRequirement")!.IsEffectivelyVisible);
        Assert.False(resume.IsEffectivelyVisible);
        await tracking.DismissLocationRequirementCommand.Execute().ToTask();
        Assert.True(resume.IsEffectivelyVisible);
        await tracking.Resume.Execute().ToTask();
        Assert.False(resume.IsEffectivelyVisible);
        await tracking.SetLocationEnabledAsync(true);
        Assert.False(view.FindControl<TextBlock>("LocationStartRequirement")!.IsEffectivelyVisible);
        Assert.True(resume.IsEffectivelyVisible);
        Assert.Equal(ProgressTrackingStatus.Paused, tracking.Progress!.Status);
        await tracking.Resume.Execute().ToTask();
        Assert.Equal(ProgressTrackingStatus.Active, tracking.Progress.Status);
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
        using Mapsui.UI.Avalonia.MapControl mapControlLifetime = view.FindControl<Mapsui.UI.Avalonia.MapControl>("MapControl")!;
        var window = new Window { Content = view, Width = 600, Height = 800 };
        window.Show();
        viewModel.SelectedRoute = new RouteAlternative(
            Guid.NewGuid(),
            "Маршрут",
            [TestData.P(0, 0), TestData.P(100, 0)],
            new RouteMetrics(100, 100, 0, 0, 0, 0, 100, null));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(view.GetVisualDescendants().OfType<TabItem>(),
            tab => Equals(tab.Header, "Прокладка") && tab.IsVisible);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(),
            button => Equals(button.Content, "К прокладке →") && button.IsEffectivelyVisible);
        Assert.False(view.FindControl<Control>("TrackingPanel")!.IsEffectivelyVisible);
        window.Close();
    }

    private static LocationController CreateController() => new(
        new UiLocationService(),
        new UiHeadingService(),
        new UiLocationPreferences());

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
        public void Emit(LocationMeasurement measurement) =>
            ((Subject<LocationMeasurement>)Measurements).OnNext(measurement);
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
