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
    // A handle drag starts after 6 DIP; a row drag requires a stationary 500 ms hold.
    private const double DragThreshold = 6;

    private const double AutoScrollEdge = 30;

    private const double AutoScrollStep = 12;

    private readonly DispatcherTimer _scrollTimer;
    private readonly DispatcherTimer _holdTimer;
    private bool _waitingForHold;
    private bool _ownsCapture;
    private PointerPressedEventArgs? _holdPress;

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
        _holdTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Input,
            (_, _) => BeginHeldDrag());
        _holdTimer.Stop();
        DetachedFromVisualTree += (_, _) => EndGesture();
    }

    private void DragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border handle && handle.DataContext is WaypointRow row
            && StartGesture(handle, row, e))
        {
            e.PreventGestureRecognition();
            _ownsCapture = true;
            e.Pointer.Capture(handle);
            e.Handled = true;
        }
    }

    private void RowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border card && card.DataContext is WaypointRow row
            && StartGesture(card, row, e))
        {
            card.Focus();
            // Leave panning available until the user holds still for 500 ms.
            _waitingForHold = true;
            _holdPress = e;
            _holdTimer.Start();
        }
    }

    private bool StartGesture(Border source, WaypointRow row, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse && !e.GetCurrentPoint(source).Properties.IsLeftButtonPressed)
        {
            return false;
        }
        EndGesture();
        _pointer = e.Pointer;
        _pointId = row.Id;
        _targetIndex = row.Number - 1;
        _pressed = e.GetPosition(this);
        _card = source.Name == "CoordinateCard" ? source
            : source.GetVisualAncestors().OfType<Border>().First(b => b.Name == "CoordinateCard");
        _scroll = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        _lastInScroll = _scroll is null ? default : e.GetPosition(_scroll);
        return true;
    }

    private void BeginHeldDrag()
    {
        _holdTimer.Stop();
        if (!_waitingForHold || _pointer is null || _card is null)
        {
            return;
        }
        _waitingForHold = false;
        _holdPress?.PreventGestureRecognition();
        _holdPress = null;
        _ownsCapture = true;
        _pointer.Capture(_card);
        _dragging = true;
        _card.Classes.Add("dragging");
        UpdateTarget(_pressed);
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

        if (_waitingForHold)
        {
            if (Math.Sqrt(Math.Pow(point.X - _pressed.X, 2) + Math.Pow(point.Y - _pressed.Y, 2)) >= DragThreshold)
            {
                EndGesture();
            }
            return;
        }
        if (!_dragging && Math.Sqrt(Math.Pow(point.X - _pressed.X, 2) + Math.Pow(point.Y - _pressed.Y, 2)) < DragThreshold)
        {
            return;
        }

        _dragging = true;
        _card.Classes.Add("dragging");
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

    private async void DragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pointer?.Id != e.Pointer.Id)
        {
            return;
        }

        var request = _dragging ? new WaypointReorder(_pointId, _targetIndex) : null;
        var selectedId = _pointId;
        EndGesture();
        e.Handled = true;
        if (request is not null)
        {
            if (ViewModel is { } vm)
            {
                await vm.ReorderPoints.Execute(request);
            }
        }
        else
        {
            ViewModel?.SelectPoint.Execute(selectedId).Subscribe().Dispose();
        }
    }

    private void RowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && sender is Border { DataContext: WaypointRow row })
        {
            ViewModel?.SelectPoint.Execute(row.Id).Subscribe().Dispose();
            e.Handled = true;
        }
    }

    private void RowExited(object? sender, PointerEventArgs e)
    {
        if (_waitingForHold && _pointer?.Id == e.Pointer.Id)
        {
            EndGesture();
        }
    }

    private void DragCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndGesture();

    private void EndGesture()
    {
        var pointer = _pointer;
        _pointer = null;
        _scrollTimer.Stop();
        _holdTimer.Stop();
        _waitingForHold = false;
        _holdPress = null;
        _targetCard?.Classes.Remove("drop-target");
        if (_card is not null)
        {
            _card.Classes.Remove("dragging");
            _card.Opacity = 1;
            _card.RenderTransform = null;
        }

        _card = null;
        _targetCard = null;
        _scroll = null;
        _dragging = false;
        var releaseCapture = _ownsCapture;
        _ownsCapture = false;
        if (releaseCapture)
        {
            pointer?.Capture(null);
        }
    }
}
