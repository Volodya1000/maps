using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace CableRoutePlanner.ViewModels;

public sealed class ShellViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _lifetime = new();

    public RoutingState Router
    {
        get;
    }

    [Reactive]
    public bool IsMap
    {
        get; private set;
    }

    [Reactive]
    public bool IsSettings
    {
        get; private set;
    }

    public ReactiveCommand<Unit, IRoutableViewModel> ShowMap
    {
        get;
    }

    public ReactiveCommand<Unit, IRoutableViewModel> ShowSettings
    {
        get;
    }

    public ReactiveCommand<Unit, IRoutableViewModel> Back => Router.NavigateBack;

    public ShellViewModel(IScreen screen, MapViewModel map, SettingsViewModel settings)
    {
        Router = screen.Router;
        ShowMap = Command(map);
        ShowSettings = Command(settings);
        Router.CurrentViewModel.Subscribe(vm =>
        {
            IsMap = vm is MapViewModel;
            IsSettings = vm is SettingsViewModel;
        }).DisposeWith(_lifetime);
        Router.NavigateAndReset.Execute(map).Subscribe().DisposeWith(_lifetime);
    }

    private ReactiveCommand<Unit, IRoutableViewModel> Command(IRoutableViewModel vm) => ReactiveCommand.CreateFromObservable(() =>
    {
        if (ReferenceEquals(Router.GetCurrentViewModel(), vm))
        {
            return Observable.Return(vm);
        }

        while (Router.NavigationStack.Count >= 2)
        {
            Router.NavigationStack.RemoveAt(0);
        }

        return Router.Navigate.Execute(vm);
    }).DisposeWith(_lifetime);

    public void Dispose() => _lifetime.Dispose();
}
