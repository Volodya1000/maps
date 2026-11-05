using Avalonia.ReactiveUI;
using CableRoutePlanner.ViewModels;

namespace CableRoutePlanner.UI.Views;

public partial class RouteChoicesView : ReactiveUserControl<MapViewModel>
{
    public RouteChoicesView() => InitializeComponent();
}
