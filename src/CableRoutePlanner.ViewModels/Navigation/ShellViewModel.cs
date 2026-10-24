using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace CableRoutePlanner.ViewModels;

public sealed class ShellViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _lifetime = new();
    private readonly SettingsViewModel _settings;
    private Func<IObservable<IRoutableViewModel>>? _pendingNavigation;

    [Reactive]
    public bool ShowUnsavedWarning
    {
        get; private set;
    }
    public ReactiveCommand<Unit, Unit> StayOnSettings
    {
        get;
    }
    public ReactiveCommand<Unit, IRoutableViewModel> DiscardAndLeave
    {
        get;
    }

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

    public ReactiveCommand<Unit, IRoutableViewModel> Back
    {
        get;
    }

    public ShellViewModel(IScreen screen, MapViewModel map, SettingsViewModel settings)
    {
        Router = screen.Router;
        _settings = settings;
        StayOnSettings = ReactiveCommand.Create(() =>
        {
            _pendingNavigation = null;
            ShowUnsavedWarning = false;
        }).DisposeWith(_lifetime);
        DiscardAndLeave = ReactiveCommand.CreateFromObservable(() =>
        {
            var navigate = _pendingNavigation;
            _pendingNavigation = null;
            ShowUnsavedWarning = false;
            _settings.Reload();
            return navigate?.Invoke() ?? Observable.Return(Router.GetCurrentViewModel()!);
        }).DisposeWith(_lifetime);
        Back = ReactiveCommand.CreateFromObservable(() => Guard(() =>
        {
            if (Router.NavigationStack.Count > 1 && Router.NavigationStack[^2] is SettingsViewModel)
            {
                _settings.Reload();
            }
            return Router.NavigateBack.Execute();
        }), Router.NavigateBack.CanExecute).DisposeWith(_lifetime);
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

        return Guard(() =>
        {
            if (vm is SettingsViewModel)
            {
                _settings.Reload();
            }
            while (Router.NavigationStack.Count >= 2)
            {
                Router.NavigationStack.RemoveAt(0);
            }
            return Router.Navigate.Execute(vm);
        });
    }).DisposeWith(_lifetime);

    public bool RequestClose(Action close)
    {
        if (Router.GetCurrentViewModel() is not SettingsViewModel || !_settings.HasChanges)
        {
            return true;
        }
        Guard(() =>
        {
            close();
            return Observable.Return(Router.GetCurrentViewModel()!);
        });
        return false;
    }

    private IObservable<IRoutableViewModel> Guard(Func<IObservable<IRoutableViewModel>> navigate)
    {
        if (Router.GetCurrentViewModel() is SettingsViewModel && _settings.HasChanges)
        {
            _pendingNavigation = navigate;
            ShowUnsavedWarning = true;
            return Observable.Return<IRoutableViewModel>(_settings);
        }
        _pendingNavigation = null;
        ShowUnsavedWarning = false;
        return navigate();
    }

    public void Dispose() => _lifetime.Dispose();
}
