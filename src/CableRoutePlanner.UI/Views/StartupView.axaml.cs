using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using CableRoutePlanner.ViewModels;
using ReactiveUI;

namespace CableRoutePlanner.UI.Views;

public partial class StartupView : ReactiveUserControl<StartupViewModel>
{
    public StartupView(StartupViewModel vm, Func<ShellView> shellFactory)
    {
        InitializeComponent();
        ViewModel = vm;
        this.WhenActivated(disposables =>
        {
            vm.WhenAnyValue(value => value.Shell).Where(shell => shell is not null).Take(1).Subscribe(_ => RootHost.Content = shellFactory()).DisposeWith(disposables);
            if (vm.Shell is null)
            {
                vm.Load.Execute().Subscribe().DisposeWith(disposables);
            }
        });
    }
}
