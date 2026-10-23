using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
namespace CableRoutePlanner.ViewModels;
public sealed class ShellViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _lifetime = new();
    public RoutingState Router { get; }
    [Reactive] public bool IsMap { get; private set; }
    [Reactive] public bool IsSettings { get; private set; }
    [Reactive] public bool IsAbout { get; private set; }
    public ReactiveCommand<Unit, IRoutableViewModel> ShowMap { get; }
    public ReactiveCommand<Unit, IRoutableViewModel> ShowSettings { get; }
    public ReactiveCommand<Unit, IRoutableViewModel> ShowAbout { get; }
    public ReactiveCommand<Unit, IRoutableViewModel> Back => Router.NavigateBack;
    public ShellViewModel(IScreen screen, MapViewModel map, SettingsViewModel settings, AboutViewModel about)
    {
        Router = screen.Router;
        ShowMap = Command(map); ShowSettings = Command(settings); ShowAbout = Command(about);
        Router.CurrentViewModel.Subscribe(vm =>
        { IsMap = vm is MapViewModel; IsSettings = vm is SettingsViewModel; IsAbout = vm is AboutViewModel; }).DisposeWith(_lifetime);
        Router.NavigateAndReset.Execute(map).Subscribe().DisposeWith(_lifetime);
    }
    private ReactiveCommand<Unit, IRoutableViewModel> Command(IRoutableViewModel vm) =>
        ReactiveCommand.CreateFromObservable(() =>
        {
            if (ReferenceEquals(Router.GetCurrentViewModel(), vm)) return Observable.Return(vm);
            // At most three existing screen instances, with a bounded history.
            while (Router.NavigationStack.Count >= 3) Router.NavigationStack.RemoveAt(0);
            return Router.Navigate.Execute(vm);
        }).DisposeWith(_lifetime);
    public void Dispose() => _lifetime.Dispose();
}
