using Avalonia.ReactiveUI;
using CableRoutePlanner.ViewModels;

namespace CableRoutePlanner.UI.Views;

public partial class TrackingPanelView : ReactiveUserControl<TrackingViewModel>
{
    public TrackingPanelView() => InitializeComponent();
}
