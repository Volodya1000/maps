using System.Reactive.Subjects;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
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
    public void AndroidMapShowsLocationAndTraversalControls()
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

        Assert.True(view.FindControl<Button>("LocationButton")!.IsEffectivelyVisible);
        Assert.True(view.FindControl<Control>("TrackingPanel")!.IsEffectivelyVisible);
        Assert.NotNull(view.FindControl<Button>("StartTraversalButton"));
        Assert.NotNull(view.FindControl<Button>("PauseTraversalButton"));
        Assert.NotNull(view.FindControl<Button>("ResumeTraversalButton"));
        Assert.NotNull(view.FindControl<Button>("FinishTraversalButton"));
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
