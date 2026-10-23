using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using CableRoutePlanner.ViewModels;
using ReactiveUI;

namespace CableRoutePlanner.UI.Views;

public partial class ShellView : ReactiveUserControl<ShellViewModel>
{
    public ShellView(ShellViewModel vm, TypedViewLocator locator)
    {
        InitializeComponent();
        RouteHost.ViewLocator = locator;
        ViewModel = vm;
        this.WhenActivated(lifetime =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
            {
                return;
            }
            async void BackRequested(object? sender, RoutedEventArgs e)
            {
                if (((ICommand)vm.Back).CanExecute(null))
                {
                    e.Handled = true;
                    await vm.Back.Execute();
                }
            }
            topLevel.BackRequested += BackRequested;
            Disposable.Create(() => topLevel.BackRequested -= BackRequested).DisposeWith(lifetime);
            if (topLevel is Window window)
            {
                void Closing(object? sender, WindowClosingEventArgs e)
                {
                    if (!vm.RequestClose(window.Close))
                    {
                        e.Cancel = true;
                    }
                }
                window.Closing += Closing;
                Disposable.Create(() => window.Closing -= Closing).DisposeWith(lifetime);
            }
        });
    }
}
