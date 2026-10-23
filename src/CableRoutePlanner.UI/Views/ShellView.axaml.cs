using Avalonia.ReactiveUI;
using CableRoutePlanner.ViewModels;
namespace CableRoutePlanner.UI.Views;
public partial class ShellView : ReactiveUserControl<ShellViewModel>
{
    public ShellView(ShellViewModel vm, TypedViewLocator locator)
    {
        InitializeComponent(); ViewModel = vm; RouteHost.ViewLocator = locator;
    }
}
