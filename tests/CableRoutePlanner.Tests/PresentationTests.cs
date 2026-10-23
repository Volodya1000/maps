using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.ViewModels;
using ReactiveUI;
using Xunit;
namespace CableRoutePlanner.Tests;
public sealed class PresentationTests
{
    [Fact] public void FodyWeavingRaisesPropertyChangedAfterRealCompilation()
    {
        using var vm=new SettingsViewModel(new NavigationHost(),new MemorySettings());
        var names=new List<string?>();vm.PropertyChanged+=(_,e)=>names.Add(e.PropertyName);
        vm.CellSize=37;vm.ReservePercent=8;
        Assert.Contains(nameof(vm.CellSize),names);Assert.Contains(nameof(vm.ReservePercent),names);
    }
    [Fact] public async Task PointChangesInvalidateRoutesAndRenumberList()
    {
        var data=new MemoryData();using var vm=CreateVm(TestData.Planner(),data);
        await vm.AddPoint.Execute(TestData.P(50,150));await vm.AddPoint.Execute(TestData.P(260,150));
        await vm.BuildRoutes.Execute();Assert.NotEmpty(vm.Alternatives);
        vm.MovePoint(vm.Points[0].Id,TestData.P(55,155));Assert.Empty(vm.Alternatives);Assert.Null(vm.SelectedRoute);
        vm.SelectedPoint=vm.Points[0];await vm.DeletePoint.Execute();Assert.Equal(1,vm.Points[0].Number);Assert.False(vm.CanBuild);
    }
    [Fact] public async Task ClipboardUsesSelectedPointAndOrderIsControlledByList()
    {
        var clipboard=new MemoryClipboard();var session=new PlanningSession(TestData.Planner());
        using var vm=new MapViewModel(new NavigationHost(),session,new MemoryData(),new MemorySettings(),clipboard);
        await vm.AddPoint.Execute(TestData.P(50,150));await vm.AddPoint.Execute(TestData.P(260,150));
        var last=vm.Points[1];vm.SelectedPoint=last;await vm.MoveUp.Execute();Assert.Equal(last.Id,vm.Points[0].Id);
        await vm.CopyCoordinates.Execute();Assert.Equal(last.Coordinate.ToString(),clipboard.Text);
    }
    [Fact] public async Task ChangedInputCannotPublishLateCalculation()
    {
        var pending=new TaskCompletionSource<IReadOnlyList<RouteAlternative>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session=new PlanningSession(new DeferredPlanner(pending.Task));session.Add(TestData.P(50,150));session.Add(TestData.P(260,150));
        var build=session.BuildAsync(TestData.Options,default);session.Move(session.Points[0].Id,TestData.P(55,155));pending.SetResult([]);
        await Assert.ThrowsAsync<PlanningException>(()=>build);
    }
    [Fact] public async Task NavigationReusesScreenInstancesAndBoundsHistory()
    {
        var screen=new NavigationHost();var data=new MemoryData();using var map=CreateVm(TestData.Planner(),data,screen);
        using var settings=new SettingsViewModel(screen,new MemorySettings());var about=new AboutViewModel(screen,data);
        using var shell=new ShellViewModel(screen,map,settings,about);
        for(var i=0;i<100;i++){await shell.ShowSettings.Execute();await shell.ShowAbout.Execute();await shell.ShowMap.Execute();}
        Assert.InRange(screen.Router.NavigationStack.Count,1,3);Assert.Same(map,screen.Router.GetCurrentViewModel());Assert.True(shell.IsMap);
        await shell.Back.Execute();Assert.Same(about,screen.Router.GetCurrentViewModel());
    }
    private static MapViewModel CreateVm(IRoutePlanner planner,IGeodataRepository data,IScreen? screen=null) =>
        new(screen??new NavigationHost(),new PlanningSession(planner),data,new MemorySettings(),new MemoryClipboard());
    private sealed class DeferredPlanner(Task<IReadOnlyList<RouteAlternative>> result) : IRoutePlanner
    {
        public Task<IReadOnlyList<RouteAlternative>> PlanAsync(IReadOnlyList<GeoCoordinate> p,RoutingOptions o,CancellationToken ct)=>result;
    }
}
