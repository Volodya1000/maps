using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CableRoutePlanner.Tests;

// Avalonia 11.3 has no public headless touch API. Keep the framework-specific
// bridge here; scenarios interact with real routed pointer events.
internal sealed class HeadlessTouch(Window window) : IDisposable
{
    private readonly TouchDevice _device = (TouchDevice)Activator.CreateInstance(typeof(TouchDevice))!;
    private static readonly MethodInfo Process = typeof(TopLevel).GetMethod("HandleInput", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly ConstructorInfo Constructor = typeof(RawTouchEventArgs).GetConstructors().Single(candidate =>
        candidate.GetParameters().Length == 7 && candidate.GetParameters()[4].ParameterType == typeof(Point));
    private ulong _timestamp;

    public void Send(RawPointerEventType type, Point position)
    {
        Present();
        object input = Constructor.Invoke(new object[]
        {
            _device, ++_timestamp, window, type, position, RawInputModifiers.None, 1L
        });
        Process.Invoke(window, new[] { input });
        Dispatcher.UIThread.RunJobs();
    }

    public static void Present()
    {
        // Same ordering as Avalonia.Headless's mouse injection helpers: rendering
        // schedules dispatcher work as well as consuming pending layout work.
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    public static async Task UntilAsync(Func<bool> ready, string message)
    {
        Stopwatch timer = Stopwatch.StartNew();
        do
        {
            Present();
            if (ready())
            {
                return;
            }
            await Task.Delay(10);
        } while (timer.Elapsed < TimeSpan.FromSeconds(5));
        throw new TimeoutException(message);
    }

    public async Task<Point> TargetAsync(Control control)
    {
        control.BringIntoView();
        Point position = default;
        await UntilAsync(() =>
        {
            Point? point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
            if (point is null)
            {
                return false;
            }
            position = point.Value;
            IInputElement? hit = window.InputHitTest(position);
            return ReferenceEquals(hit, control) || (hit as Visual)?.GetVisualAncestors().Contains(control) == true;
        }, "The requested control did not become available for touch input.");
        return position;
    }

    public void Dispose() => ((IDisposable)_device).Dispose();
}
