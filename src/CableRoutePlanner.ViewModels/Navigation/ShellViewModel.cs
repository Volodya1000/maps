using System.Diagnostics.CodeAnalysis;
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
    private readonly MapViewModel _map;
    private Func<IObservable<IRoutableViewModel>>? _pendingNavigation;

    [Reactive]
    public bool ShowUnsavedWarning
    {
        get; private set;
    }
    public ReactiveCommand<Unit, Unit> StayOnSettings
    {
        get; private set;
    }
    public ReactiveCommand<Unit, IRoutableViewModel> DiscardAndLeave
    {
        get; private set;
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
        get; private set;
    }

    public ReactiveCommand<Unit, IRoutableViewModel> ShowSettings
    {
        get; private set;
    }

    public ReactiveCommand<Unit, IRoutableViewModel> Back
    {
        get; private set;
    }

    public ShellViewModel(IScreen screen, MapViewModel map, SettingsViewModel settings)
    {
        Router = screen.Router;
        _settings = settings;
        _map = map;
        InitializeCommands();
        SetupSubscriptions();
        Router.NavigateAndReset.Execute(_map).Subscribe().DisposeWith(_lifetime);
    }

    [MemberNotNull(nameof(StayOnSettings), nameof(DiscardAndLeave), nameof(Back), nameof(ShowMap), nameof(ShowSettings))]
    private void InitializeCommands()
    {
        StayOnSettings = ReactiveCommand.Create(Stay).DisposeWith(_lifetime);
        DiscardAndLeave = ReactiveCommand.CreateFromObservable(Discard).DisposeWith(_lifetime);
        Back = ReactiveCommand.CreateFromObservable(() => Guard(NavigateBack), Router.NavigateBack.CanExecute).DisposeWith(_lifetime);
        ShowMap = Command(_map);
        ShowSettings = Command(_settings);
    }

    private void SetupSubscriptions() => Router.CurrentViewModel.Subscribe(CurrentScreenChanged).DisposeWith(_lifetime);

    private void CurrentScreenChanged(IRoutableViewModel? vm)
    {
        IsMap = vm is MapViewModel;
        IsSettings = vm is SettingsViewModel;
    }

    private void Stay()
    {
        _pendingNavigation = null;
        ShowUnsavedWarning = false;
    }

    private IObservable<IRoutableViewModel> Discard()
    {
        var navigate = _pendingNavigation;
        Stay();
        _settings.Reload();
        return navigate?.Invoke() ?? Observable.Return(Router.GetCurrentViewModel()!);
    }

    private IObservable<IRoutableViewModel> NavigateBack()
    {
        if (Router.NavigationStack.Count > 1 && Router.NavigationStack[^2] is SettingsViewModel)
        {
            _settings.Reload();
        }
        return Router.NavigateBack.Execute();
    }

    private ReactiveCommand<Unit, IRoutableViewModel> Command(IRoutableViewModel vm) =>
        ReactiveCommand.CreateFromObservable(() => NavigateTo(vm)).DisposeWith(_lifetime);

    private IObservable<IRoutableViewModel> NavigateTo(IRoutableViewModel vm)
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
    }

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
