using Avalonia.ReactiveUI;
using CableRoutePlanner.ViewModels;

namespace CableRoutePlanner.UI.Views;

public partial class RouteElevationView : ReactiveUserControl<MapViewModel>
{
    public RouteElevationView()
    {
        InitializeComponent();
        ProfileChart.SampleSelected += OnSampleSelected;
    }

    private void OnSampleSelected(object? sender, int? index)
    {
        if (ProfileChart.Presentation is { } presentation)
        {
            ViewModel?.SelectElevationSample(presentation, index);
        }
    }
}
