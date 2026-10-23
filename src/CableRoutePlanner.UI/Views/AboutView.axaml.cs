using Avalonia.ReactiveUI;
using CableRoutePlanner.ViewModels;
namespace CableRoutePlanner.UI.Views;
public partial class AboutView : ReactiveUserControl<AboutViewModel>
{
    public AboutView() => InitializeComponent();
}
