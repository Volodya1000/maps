using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using CableRoutePlanner.ViewModels;

namespace CableRoutePlanner.UI.Views;

public partial class MapOverlayControlsView : ReactiveUserControl<MapViewModel>
{
    private readonly RotateTransform _northArrowRotation = new();

    public MapOverlayControlsView()
    {
        InitializeComponent();
        NorthArrow.RenderTransform = _northArrowRotation;
    }

    public event Action? ZoomInRequested;

    public event Action? ZoomOutRequested;

    public event Action? ResetNorthRequested;

    public event Action? CenterLocationRequested;

    public void SetNorthIndicatorRotation(double angle) => _northArrowRotation.Angle = angle;

    private void ZoomInClicked(object? sender, RoutedEventArgs e) => ZoomInRequested?.Invoke();

    private void ZoomOutClicked(object? sender, RoutedEventArgs e) => ZoomOutRequested?.Invoke();

    private void ResetNorthClicked(object? sender, RoutedEventArgs e) => ResetNorthRequested?.Invoke();

    private void CenterLocationClicked(object? sender, RoutedEventArgs e) => CenterLocationRequested?.Invoke();
}
