using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CableRoutePlanner.Domain;
using CableRoutePlanner.ViewModels;

namespace CableRoutePlanner.UI.Controls;

public sealed partial class RouteElevationChart : Control
{
    private const double TouchSlopDip = 6;

    public static readonly StyledProperty<RouteElevationPresentation?> PresentationProperty =
        AvaloniaProperty.Register<RouteElevationChart, RouteElevationPresentation?>(nameof(Presentation));

    public static readonly StyledProperty<IBrush> ElevationBrushProperty =
        AvaloniaProperty.Register<RouteElevationChart, IBrush>(nameof(ElevationBrush), Brushes.SteelBlue);

    public static readonly StyledProperty<IBrush> ElevationFillProperty =
        AvaloniaProperty.Register<RouteElevationChart, IBrush>(nameof(ElevationFill), Brushes.LightBlue);

    public static readonly StyledProperty<IBrush> GradeBrushProperty =
        AvaloniaProperty.Register<RouteElevationChart, IBrush>(nameof(GradeBrush), Brushes.SeaGreen);

    public static readonly StyledProperty<IBrush> AxisBrushProperty =
        AvaloniaProperty.Register<RouteElevationChart, IBrush>(nameof(AxisBrush), Brushes.DimGray);

    public static readonly StyledProperty<IBrush> GridBrushProperty =
        AvaloniaProperty.Register<RouteElevationChart, IBrush>(nameof(GridBrush), Brushes.LightGray);

    public static readonly StyledProperty<IBrush> CursorBrushProperty =
        AvaloniaProperty.Register<RouteElevationChart, IBrush>(nameof(CursorBrush), Brushes.DarkOrange);

    private IPointer? _touchPointer;
    private Point _touchStart;
    private bool _scrubbing;

    static RouteElevationChart()
    {
        AffectsRender<RouteElevationChart>(PresentationProperty, ElevationBrushProperty, ElevationFillProperty,
            GradeBrushProperty, AxisBrushProperty, GridBrushProperty, CursorBrushProperty);
    }

    public RouteElevationPresentation? Presentation
    {
        get => GetValue(PresentationProperty);
        set => SetValue(PresentationProperty, value);
    }

    public IBrush ElevationBrush
    {
        get => GetValue(ElevationBrushProperty);
        set => SetValue(ElevationBrushProperty, value);
    }

    public IBrush ElevationFill
    {
        get => GetValue(ElevationFillProperty);
        set => SetValue(ElevationFillProperty, value);
    }

    public IBrush GradeBrush
    {
        get => GetValue(GradeBrushProperty);
        set => SetValue(GradeBrushProperty, value);
    }

    public IBrush AxisBrush
    {
        get => GetValue(AxisBrushProperty);
        set => SetValue(AxisBrushProperty, value);
    }

    public IBrush GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public IBrush CursorBrush
    {
        get => GetValue(CursorBrushProperty);
        set => SetValue(CursorBrushProperty, value);
    }

    internal event EventHandler<int?>? SampleSelected;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PresentationProperty)
        {
            RouteElevationPresentation? previous = change.GetOldValue<RouteElevationPresentation?>();
            if (previous?.RouteId != Presentation?.RouteId || !ReferenceEquals(previous?.Profile, Presentation?.Profile))
            {
                _plot = null;
                StopTouch(release: true);
            }
        }
        else if (change.Property == ElevationBrushProperty || change.Property == GradeBrushProperty
            || change.Property == AxisBrushProperty)
        {
            _plot = null;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (GetPlot() is not { } plot || !plot.Area.Contains(e.GetPosition(this)))
        {
            return;
        }

        if (e.Pointer.Type == PointerType.Mouse)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                Focus();
                SelectAt(e.GetPosition(this).X, plot);
                e.Handled = true;
            }
        }
        else if (_touchPointer is null && e.Pointer.IsPrimary)
        {
            _touchPointer = e.Pointer;
            _touchStart = e.GetPosition(null);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (GetPlot() is not { } plot)
        {
            return;
        }

        Point position = e.GetPosition(this);
        if (e.Pointer.Type == PointerType.Mouse && plot.Area.Contains(position))
        {
            SelectAt(position.X, plot);
        }
        else if (ReferenceEquals(_touchPointer, e.Pointer) && (_scrubbing || StartScrubbing(e)))
        {
            SelectAt(position.X, plot);
            e.Handled = true;
        }
    }

    private bool StartScrubbing(PointerEventArgs e)
    {
        Vector drag = e.GetPosition(null) - _touchStart;
        double horizontal = Math.Abs(drag.X);
        double vertical = Math.Abs(drag.Y);
        if (vertical >= TouchSlopDip && vertical >= horizontal)
        {
            // Allow the sheet's ScrollGestureRecognizer to own the vertical drag.
            StopTouch(release: false);
        }
        else if (horizontal >= TouchSlopDip)
        {
            _scrubbing = true;
            e.PreventGestureRecognition();
            e.Pointer.Capture(this);
        }
        return _scrubbing;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!ReferenceEquals(_touchPointer, e.Pointer))
        {
            return;
        }

        bool scrubbing = _scrubbing;
        Point position = e.GetPosition(this);
        StopTouch(release: true);
        if (GetPlot() is { } plot && (scrubbing || plot.Area.Contains(position)))
        {
            SelectAt(position.X, plot);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (ReferenceEquals(_touchPointer, e.Pointer))
        {
            StopTouch(release: false);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopTouch(release: true);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyModifiers != KeyModifiers.None || Presentation is not { HasChart: true, Profile: { } profile } state)
        {
            return;
        }

        int last = profile.Samples.Count - 1;
        int current = state.CursorIndex ?? -1;
        int? index = e.Key switch
        {
            Key.Left => Math.Max(0, current - 1),
            Key.Right => Math.Min(last, current + 1),
            Key.Home => 0,
            Key.End => last,
            _ => null
        };
        if (index.HasValue || (e.Key == Key.Escape && state.CursorIndex.HasValue))
        {
            SampleSelected?.Invoke(this, index);
            e.Handled = true;
        }
    }

    private void StopTouch(bool release)
    {
        IPointer? pointer = _touchPointer;
        _touchPointer = null;
        _scrubbing = false;
        if (release && ReferenceEquals(pointer?.Captured, this))
        {
            pointer!.Capture(null);
        }
    }

    private void SelectAt(double x, ChartPlot plot)
    {
        double fraction = Math.Clamp((x - plot.Area.Left) / plot.Area.Width, 0, 1);
        double distance = fraction * plot.Profile.HorizontalLengthMetres;
        IReadOnlyList<RouteElevationSample> samples = plot.Profile.Samples;
        int low = 0;
        int high = samples.Count - 1;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (samples[middle].DistanceMetres < distance)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        if (low > 0 && distance - samples[low - 1].DistanceMetres <= samples[low].DistanceMetres - distance)
        {
            low--;
        }
        SampleSelected?.Invoke(this, low);
    }
}
