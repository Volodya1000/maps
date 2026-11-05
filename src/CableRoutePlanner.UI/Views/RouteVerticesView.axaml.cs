using Avalonia.ReactiveUI;
using CableRoutePlanner.ViewModels;

namespace CableRoutePlanner.UI.Views;

public partial class RouteVerticesView : ReactiveUserControl<MapViewModel>
{
    public RouteVerticesView() => InitializeComponent();
}
