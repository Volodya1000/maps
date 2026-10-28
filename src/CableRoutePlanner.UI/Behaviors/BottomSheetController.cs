using System.Reactive.Disposables;
using System.Reactive;
using ReactiveUI;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace CableRoutePlanner.UI.Behaviors;

// Owns only bottom-sheet interaction; layout remains the view's responsibility.
internal sealed class BottomSheetController
{
    private const double SwipeThresholdPixels = 36;
    private IPointer? _pointer;
    private Point _start;

    public bool IsExpanded { get; private set; } = true;

    public IDisposable Bind(Control handle, Control coordinateRoot, Action changed)
    {
        var lifetime = new CompositeDisposable();
        void SetExpanded(bool expanded)
        {
            IsExpanded = expanded;
            changed();
        }
        ReactiveCommand<Unit, Unit> toggle = ReactiveCommand.Create(() => SetExpanded(!IsExpanded)).DisposeWith(lifetime);
        handle.PointerPressed += Pressed;
        handle.PointerMoved += Moved;
        handle.PointerReleased += Released;
        handle.KeyDown += KeyDown;
        Disposable.Create(() =>
        {
            handle.PointerPressed -= Pressed;
            handle.PointerMoved -= Moved;
            handle.PointerReleased -= Released;
            handle.KeyDown -= KeyDown;
            _pointer?.Capture(null);
            _pointer = null;
        }).DisposeWith(lifetime);
        return lifetime;

        void Pressed(object? sender, PointerPressedEventArgs args)
        {
            _pointer = args.Pointer;
            _start = args.GetPosition(coordinateRoot);
            args.Pointer.Capture(handle);
            args.Handled = true;
        }
        void Moved(object? sender, PointerEventArgs args)
        {
            if (_pointer?.Id == args.Pointer.Id)
            { args.Handled = true; }
        }
        void Released(object? sender, PointerReleasedEventArgs args)
        {
            if (_pointer?.Id != args.Pointer.Id)
            { return; }
            double distance = args.GetPosition(coordinateRoot).Y - _start.Y;
            _pointer = null;
            args.Pointer.Capture(null);
            args.Handled = true;
            if (Math.Abs(distance) < SwipeThresholdPixels)
            { toggle.Execute().Subscribe(); }
            else
            { SetExpanded(distance < 0); }
        }
        void KeyDown(object? sender, KeyEventArgs args)
        {
            if (args.Key is not (Key.Enter or Key.Space))
            { return; }
            toggle.Execute().Subscribe();
            args.Handled = true;
        }
    }

}
