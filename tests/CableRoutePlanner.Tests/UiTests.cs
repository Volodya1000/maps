using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.ReactiveUI;
using CableRoutePlanner.Bootstrap;
using CableRoutePlanner.UI;
using CableRoutePlanner.UI.Views;
using CableRoutePlanner.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
[assembly: AvaloniaTestApplication(typeof(CableRoutePlanner.Tests.TestApplication))]
[assembly: CollectionBehavior(DisableTestParallelization=true)]
namespace CableRoutePlanner.Tests;
public static class TestApplication
{
    public static AppBuilder BuildAvaloniaApp()=>AppBuilder.Configure<PlannerApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).UseReactiveUI();
}
public sealed class UiTests
{
    [AvaloniaFact] public void CompiledXamlAndTypedViewLocatorResolveAllRegisteredScreens()
    {
        using var provider=AppBootstrap.CreateServices(TestData.RealData,Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json"),new MemoryClipboard());
        var locator=provider.GetRequiredService<TypedViewLocator>();
        Assert.IsType<MapView>(locator.ResolveView(provider.GetRequiredService<MapViewModel>()));
        Assert.IsType<SettingsView>(locator.ResolveView(provider.GetRequiredService<SettingsViewModel>()));
        Assert.IsType<AboutView>(locator.ResolveView(provider.GetRequiredService<AboutViewModel>()));
        var shell=provider.GetRequiredService<ShellView>();var window=new Window{Content=shell};window.Show();
        Assert.NotNull(shell.ViewModel);window.Close();
    }
    [AvaloniaFact] public void MapMouseInputAddsAndMovesViewModelPoints()
    {
        using var provider=AppBootstrap.CreateServices(TestData.RealData,Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json"),new MemoryClipboard());
        var vm=provider.GetRequiredService<MapViewModel>();
        var view=(MapView)provider.GetRequiredService<TypedViewLocator>().ResolveView(vm)!;
        var window=new Window{Content=view,Width=1100,Height=800};window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.MouseDown(new Avalonia.Point(360,360),Avalonia.Input.MouseButton.Left);
        window.MouseUp(new Avalonia.Point(360,360),Avalonia.Input.MouseButton.Left);
        Assert.Single(vm.Points);
        var before=vm.Points[0].Coordinate;
        window.MouseDown(new Avalonia.Point(360,360),Avalonia.Input.MouseButton.Left);
        window.MouseMove(new Avalonia.Point(390,380));
        window.MouseUp(new Avalonia.Point(390,380),Avalonia.Input.MouseButton.Left);
        Assert.Single(vm.Points);Assert.True(before.DistanceTo(vm.Points[0].Coordinate)>1);
        window.Close();
    }
    [AvaloniaFact] public void UnknownViewModelCannotFallBackToReflection()
    {
        using var provider=AppBootstrap.CreateServices(TestData.RealData,Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json"),new MemoryClipboard());
        Assert.Throws<ArgumentException>(()=>provider.GetRequiredService<TypedViewLocator>().ResolveView(new object()));
    }
}
