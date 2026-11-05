using Avalonia.ReactiveUI;
using CableRoutePlanner.ViewModels;

namespace CableRoutePlanner.UI.Views;

public partial class TrackingPanelView : ReactiveUserControl<MapViewModel>
{
    public TrackingPanelView() => InitializeComponent();
}
