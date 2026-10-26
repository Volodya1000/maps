using ReactiveUI;
using System.Reactive.Linq;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.ViewModels;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class SettingsTests : ReactiveTestScope
{
    [Fact]
    public async Task SaveTracksPersistedValuesAndReturningToOriginalClearsChanges()
    {
        var store = new MemorySettings();
        using var vm = new SettingsViewModel(new NavigationHost(), store);
        Assert.Equal(20m, vm.CellSize);
        Assert.Equal(10m, vm.ReservePercent);
        Assert.False(vm.HasChanges);
        Assert.False(await vm.Save.CanExecute.FirstAsync());
        vm.CellSize = 35;
        Assert.True(vm.HasChanges);
        vm.CellSize = 20;
        Assert.False(vm.HasChanges);
        vm.ReservePercent = 15;
        await vm.Save.Execute();
        Assert.Equal(.15, store.Options.ReserveFraction);
        Assert.False(vm.HasChanges);
        Assert.False(await vm.Save.CanExecute.FirstAsync());
        vm.ReservePercent = 10;
        Assert.True(vm.HasChanges);
        vm.ReservePercent = 15;
        Assert.False(vm.HasChanges);
        vm.CellSize = null;
        Assert.True(vm.HasChanges);
        Assert.False(vm.CanSave);
    }

    [Fact]
    public async Task FailedSaveKeepsEditsAndPersistedBaseline()
    {
        var store = new FailingSettings();
        using var vm = new SettingsViewModel(new NavigationHost(), store);
        vm.CellSize = 42;
        await vm.Save.Execute();
        Assert.Equal(42m, vm.CellSize);
        Assert.True(vm.HasChanges);
        Assert.True(vm.CanSave);
        Assert.Contains("Ошибка сохранения", vm.Status);
        vm.CellSize = 25;
        Assert.False(vm.HasChanges);
    }

    [Theory]
    [InlineData(4, 10)]
    [InlineData(201, 10)]
    [InlineData(20, -1)]
    [InlineData(20, 101)]
    public void ValuesOutsideSupportedRangesCannotBeSaved(decimal cellSize, decimal reservePercent)
    {
        // Arrange
        using var vm = new SettingsViewModel(new NavigationHost(), new MemorySettings());
        vm.CellSize = cellSize;
        vm.ReservePercent = reservePercent;

        // Act
        var canSave = vm.CanSave;

        // Assert
        Assert.False(canSave);
    }

    [Fact]
    public async Task NavigationWarnsAndOnlyDiscardsAfterExplicitChoice()
    {
        var screen = new NavigationHost();
        var store = new MemorySettings();
        using var settings = new SettingsViewModel(screen, store);
        using var map = new MapViewModel(screen, new PlanningSession(TestData.Planner(), TestData.AllowAllValidator),
            new MemoryData(), store, new MemoryClipboard());
        using var shell = new ShellViewModel(screen, map, settings);
        // Opening reads the actual store, even though this VM is reused.
        store.Save(new RoutingOptions(30, 12345, .12));
        await shell.ShowSettings.Execute();
        Assert.Equal(30m, settings.CellSize);
        settings.CellSize = 40;
        await shell.ShowMap.Execute();
        Assert.Same(settings, screen.Router.GetCurrentViewModel());
        Assert.True(shell.ShowUnsavedWarning);
        await shell.StayOnSettings.Execute();
        Assert.Equal(40m, settings.CellSize);
        Assert.False(shell.ShowUnsavedWarning);
        await shell.Back.Execute();
        Assert.True(shell.ShowUnsavedWarning);
        await shell.DiscardAndLeave.Execute();
        Assert.Same(map, screen.Router.GetCurrentViewModel());
        Assert.Equal(40m, settings.CellSize);
        Assert.True(settings.HasChanges);
        await shell.ShowSettings.Execute();
        Assert.Equal(30m, settings.CellSize);
        Assert.False(settings.HasChanges);
        Assert.Equal(30m, settings.CellSize);
        Assert.False(settings.HasChanges);
        settings.ReservePercent = 20;
        await settings.Save.Execute();
        Assert.Equal(12345, store.Options.MaxNodes);
        await shell.ShowMap.Execute();
        Assert.Same(map, screen.Router.GetCurrentViewModel());
        Assert.False(shell.ShowUnsavedWarning);
    }

    private sealed class FailingSettings : ISettingsStore
    {
        public RoutingOptions Load() => new();
        public void Save(RoutingOptions options) => throw new IOException("disk full");
    }
}
