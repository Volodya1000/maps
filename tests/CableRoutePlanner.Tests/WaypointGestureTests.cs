using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using CableRoutePlanner.Application;
using CableRoutePlanner.UI.Controls;
using CableRoutePlanner.ViewModels;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class WaypointGestureTests
{
    [AvaloniaFact]
    public async Task HandleDragChangesOrderWithoutChangingPointIdentity()
    {
        using var fixture = await WaypointListFixture.CreateAsync();
        var ids = fixture.ViewModel.Points.Select(point => point.Id).ToArray();
        var start = await fixture.Touch.TargetAsync(fixture.Handle(0));
        var end = await fixture.Touch.TargetAsync(fixture.Handle(2));

        fixture.Touch.Send(RawPointerEventType.TouchBegin, start);
        fixture.Touch.Send(RawPointerEventType.TouchUpdate, end);
        fixture.Touch.Send(RawPointerEventType.TouchEnd, end);

        Assert.Equal(new[] { ids[1], ids[2], ids[0], ids[3], ids[4], ids[5] }, fixture.ViewModel.Points.Select(point => point.Id));
        Assert.Equal(Enumerable.Range(1, 6), fixture.ViewModel.Points.Select(point => point.Number));
    }

    [AvaloniaFact]
    public async Task DeleteAfterReorderingRemovesTheTappedPointRatherThanTheSelectedPoint()
    {
        using var fixture = await WaypointListFixture.CreateAsync();
        var vm = fixture.ViewModel;
        var ids = vm.Points.Select(point => point.Id).ToArray();
        vm.SelectedPoint = vm.Points[0];
        await vm.ReorderPoints.Execute(new WaypointReorder(ids[0], 2));
        HeadlessTouch.Present();
        var delete = fixture.List.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "×") && ((WaypointRow)button.DataContext!).Id == ids[2]);
        var position = await fixture.Touch.TargetAsync(delete);

        fixture.Touch.Send(RawPointerEventType.TouchBegin, position);
        fixture.Touch.Send(RawPointerEventType.TouchEnd, position);

        Assert.Equal(new[] { ids[1], ids[0], ids[3], ids[4], ids[5] }, vm.Points.Select(point => point.Id));
        Assert.Equal(ids[0], vm.SelectedPoint?.Id);
    }

    [AvaloniaFact]
    public async Task HoldingRowShowsFeedbackAndChangesOrder()
    {
        using var fixture = await WaypointListFixture.CreateAsync();
        var ids = fixture.ViewModel.Points.Select(point => point.Id).ToArray();
        var card = fixture.Card(0);
        var draggedRow = fixture.Row(0);
        var normalAppearance = (card.Background, card.BoxShadow, card.Opacity, card.RenderTransform, card.BorderBrush, card.BorderThickness);
        var start = card.TranslatePoint(new Point(40, 30), fixture.Window)!.Value;
        var end = fixture.Card(2).TranslatePoint(new Point(40, 30), fixture.Window)!.Value;

        fixture.Touch.Send(RawPointerEventType.TouchBegin, start);
        await HeadlessTouch.UntilAsync(() => !Equals(normalAppearance,
            (card.Background, card.BoxShadow, card.Opacity, card.RenderTransform, card.BorderBrush, card.BorderThickness))
            && draggedRow.ZIndex > 0,
            "Holding did not show grab feedback.");
        fixture.Touch.Send(RawPointerEventType.TouchUpdate, end);
        fixture.Touch.Send(RawPointerEventType.TouchEnd, end);

        Assert.Equal(new[] { ids[1], ids[2], ids[0], ids[3], ids[4], ids[5] }, fixture.ViewModel.Points.Select(point => point.Id));
        Assert.Equal(0, draggedRow.ZIndex);
    }

    [AvaloniaFact]
    public async Task OrdinarySwipeScrollsWithoutReordering()
    {
        using var fixture = await WaypointListFixture.CreateAsync();
        var ids = fixture.ViewModel.Points.Select(point => point.Id).ToArray();
        var start = fixture.Card(2).TranslatePoint(new Point(40, 30), fixture.Window)!.Value;

        fixture.Touch.Send(RawPointerEventType.TouchBegin, start);
        fixture.Touch.Send(RawPointerEventType.TouchUpdate, start - new Point(0, 50));
        // This duration is deliberate input: scrolling must stay scrolling even
        // when the contact lasts longer than the 500 ms hold threshold.
        await Task.Delay(600);
        fixture.Touch.Send(RawPointerEventType.TouchUpdate, start - new Point(0, 90));
        fixture.Touch.Send(RawPointerEventType.TouchEnd, start - new Point(0, 90));

        Assert.Equal(ids, fixture.ViewModel.Points.Select(point => point.Id));
        Assert.True(fixture.Scroll.Offset.Y > 0, "A normal swipe should scroll the list.");
    }

    private sealed class WaypointListFixture : IDisposable
    {
        public MapViewModel ViewModel
        {
            get;
        } = new(new NavigationHost(), new PlanningSession(TestData.Planner()),
            new MemoryData(), new MemorySettings(), new MemoryClipboard());
        public WaypointList List
        {
            get;
        }
        public ScrollViewer Scroll
        {
            get;
        }
        public Window Window
        {
            get;
        }
        public HeadlessTouch Touch
        {
            get;
        }

        private WaypointListFixture()
        {
            List = new WaypointList { ViewModel = ViewModel };
            Scroll = new ScrollViewer { Content = List };
            Window = new Window { Content = Scroll, Width = 380, Height = 220 };
            Touch = new HeadlessTouch(Window);
        }

        public static async Task<WaypointListFixture> CreateAsync()
        {
            var fixture = new WaypointListFixture();
            for (var index = 0; index < 6; index++)
            {
                await fixture.ViewModel.AddPoint.Execute(TestData.P(40 + index * 40, 150));
            }
            fixture.Window.Show();
            HeadlessTouch.Present();
            return fixture;
        }

        public Border Card(int index) => List.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Name == "CoordinateCard").ElementAt(index);

        public Control Row(int index) => List.GetVisualDescendants()
            .OfType<ItemsControl>()
            .Single(control => control.Name == "Rows")
            .ContainerFromIndex(index)!;
        public Border Handle(int index) => Card(index).GetVisualDescendants().OfType<Border>()
            .Single(border => border.Name == "DragHandle");

        public void Dispose()
        {
            Window.Close();
            Touch.Dispose();
            ViewModel.Dispose();
        }
    }
}
