using System.Reactive.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CableRoutePlanner.Application;
using CableRoutePlanner.UI.Controls;
using CableRoutePlanner.ViewModels;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class WaypointGestureTests
{
    [AvaloniaFact]
    public async Task TouchDragReordersCoordinatesAndRedCrossDeletesItsOwnRow()
    {
        using var vm = new MapViewModel(
            new NavigationHost(), new PlanningSession(TestData.Planner()),
            new MemoryData(), new MemorySettings(), new MemoryClipboard());
        await vm.AddPoint.Execute(TestData.P(40, 150));
        await vm.AddPoint.Execute(TestData.P(200, 150));
        await vm.AddPoint.Execute(TestData.P(300, 150));
        await vm.AddPoint.Execute(TestData.P(350, 150));
        var ids = vm.Points.Select(point => point.Id).ToArray();
        var list = new WaypointList { ViewModel = vm };
        var scroll = new ScrollViewer { Content = list };
        var window = new Window { Content = scroll, Width = 380, Height = 220 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var handles = list.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Name == "DragHandle").ToArray();
        Assert.Equal(4, handles.Length);
        var start = handles[0].TranslatePoint(new Point(18, 30), window)!.Value;
        var end = handles[2].TranslatePoint(new Point(18, 30), window)!.Value;
        // Avalonia.Headless has mouse helpers but no public touch injection API in 11.3.
        // Reflection is confined to this test to inject real raw touch events into Avalonia.
        using var device = (TouchDevice)Activator.CreateInstance(typeof(TouchDevice))!;
        var processInput = typeof(TopLevel).GetMethod("HandleInput", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Touch(RawPointerEventType type, Point position, ulong timestamp)
        {
            var constructor = typeof(RawTouchEventArgs).GetConstructors().Single(candidate =>
                candidate.GetParameters().Length == 7 && candidate.GetParameters()[4].ParameterType == typeof(Point));
            var input = constructor.Invoke(new object[]
            {
                device, timestamp, window, type, position, RawInputModifiers.None, 1L
            });
            processInput.Invoke(window, new[] { input });
            Dispatcher.UIThread.RunJobs();
        }

        Touch(RawPointerEventType.TouchBegin, start, 1);
        Touch(RawPointerEventType.TouchUpdate, end, 2);
        Touch(RawPointerEventType.TouchEnd, end, 3);
        Assert.Equal(new[] { ids[1], ids[2], ids[0], ids[3] }, vm.Points.Select(point => point.Id));
        Assert.Equal(new[] { 1, 2, 3, 4 }, vm.Points.Select(point => point.Number));

        var cross = list.GetVisualDescendants().OfType<Button>()
            .First(button => Equals(button.Content, "×") && ((WaypointRow)button.DataContext!).Id == ids[2]);
        var click = cross.TranslatePoint(new Point(cross.Bounds.Width / 2, cross.Bounds.Height / 2), window)!.Value;
        window.MouseDown(click, MouseButton.Left);
        window.MouseUp(click, MouseButton.Left);
        Assert.Equal(new[] { ids[1], ids[0], ids[3] }, vm.Points.Select(point => point.Id));
        window.Close();
    }
}
