using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CableRoutePlanner.ViewModels;

namespace CableRoutePlanner.UI.Controls;

public partial class WaypointList : ReactiveUserControl<MapViewModel>
{
    // DIP thresholds: a deliberate drag starts after 6 DIP; handles are at least 36×60 DIP.
    private const double DragThreshold = 6;

    private const double AutoScrollEdge = 30;

    private const double AutoScrollStep = 12;

    private readonly DispatcherTimer _scrollTimer;

    private IPointer? _pointer;

    private Guid _pointId;

    private Border? _card;

    private Border? _targetCard;

    private ScrollViewer? _scroll;

    private Point _pressed;

    private Point _lastInScroll;

    private int _targetIndex;

    private bool _dragging;

    public WaypointList()
    {
        InitializeComponent();
        _scrollTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(40),
            DispatcherPriority.Input,
            (_, _) => AutoScroll());
        _scrollTimer.Stop();
        DetachedFromVisualTree += (_, _) => EndGesture();
    }

    private void DragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border handle || handle.DataContext is not WaypointRow row)
        {
            return;
        }

        if (e.Pointer.Type == PointerType.Mouse && !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        EndGesture();
        // The handle owns this gesture; otherwise ScrollViewer captures it for panning.
        e.PreventGestureRecognition();
        _pointer = e.Pointer;
        _pointId = row.Id;
        _targetIndex = row.Number - 1;
        _pressed = e.GetPosition(this);
        _card = handle.GetVisualAncestors().OfType<Border>().First(b => b.Name == "CoordinateCard");
        _scroll = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        _lastInScroll = _scroll is null ? default : e.GetPosition(_scroll);
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void DragMoved(object? sender, PointerEventArgs e)
    {
        if (_pointer?.Id != e.Pointer.Id || _card is null)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (_scroll is not null)
        {
            _lastInScroll = e.GetPosition(_scroll);
        }

        if (!_dragging && Math.Abs(point.Y - _pressed.Y) < DragThreshold)
        {
            return;
        }

        _dragging = true;
        _card.Opacity = 0.8;
        _card.RenderTransform = new TranslateTransform(0, point.Y - _pressed.Y);
        UpdateTarget(point);
        _scrollTimer.Start();
        e.Handled = true;
    }

    private void UpdateTarget(Point point)
    {
        if (ViewModel is null)
        {
            return;
        }

        var nearest = double.PositiveInfinity;
        for (var index = 0; index < ViewModel.Points.Count; index++)
        {
            if (Rows.ContainerFromIndex(index) is not Control row)
            {
                continue;
            }

            var origin = row.TranslatePoint(default, this);
            if (origin is null)
            {
                continue;
            }

            var distance = Math.Abs(point.Y - origin.Value.Y - row.Bounds.Height / 2);
            if (distance >= nearest)
            {
                continue;
            }

            nearest = distance;
            _targetIndex = index;
        }

        _targetCard?.Classes.Remove("drop-target");
        _targetCard = (Rows.ContainerFromIndex(_targetIndex) as Control)?.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "CoordinateCard");
        _targetCard?.Classes.Add("drop-target");
    }

    private void AutoScroll()
    {
        if (!_dragging || _scroll is null)
        {
            return;
        }

        var direction = _lastInScroll.Y < AutoScrollEdge ? -1 : _lastInScroll.Y > _scroll.Bounds.Height - AutoScrollEdge ? 1 : 0;
        if (direction == 0)
        {
            return;
        }

        _scroll.Offset = new Vector(_scroll.Offset.X, Math.Max(0, _scroll.Offset.Y + direction * AutoScrollStep));
        var translated = _scroll.TranslatePoint(_lastInScroll, this);
        if (translated is { } point)
        {
            UpdateTarget(point);
            if (_card is not null)
            {
                _card.RenderTransform = new TranslateTransform(0, point.Y - _pressed.Y);
            }
        }
    }

    private void DragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pointer?.Id != e.Pointer.Id)
        {
            return;
        }

        var request = _dragging ? new WaypointReorder(_pointId, _targetIndex) : null;
        EndGesture();
        if (request is not null)
        {
            ViewModel?.ReorderPoints.Execute(request).Subscribe().Dispose();
        }

        e.Handled = true;
    }

    private void DragCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndGesture();

    private void EndGesture()
    {
        var pointer = _pointer;
        _pointer = null;
        _scrollTimer.Stop();
        _targetCard?.Classes.Remove("drop-target");
        if (_card is not null)
        {
            _card.Opacity = 1;
            _card.RenderTransform = null;
        }

        _card = null;
        _targetCard = null;
        _scroll = null;
        _dragging = false;
        pointer?.Capture(null);
    }
}
