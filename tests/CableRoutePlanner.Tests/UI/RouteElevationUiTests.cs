using System.Reactive.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using CableRoutePlanner.UI.Controls;
using Avalonia.VisualTree;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.UI.MapAdapters;
using CableRoutePlanner.UI.Views;
using CableRoutePlanner.ViewModels;
using Mapsui.Layers;
using Mapsui.Nts;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class RouteElevationUiTests
{
    [AvaloniaFact]
    public void RouteWithoutElevationShowsMissingProfileAndLabelsPlanarCableLength()
    {
        using ProfileWindow fixture = new(1100);
        fixture.ShowRoute(CreateRoute("Тестовый коридор", null));

        IEnumerable<string?> visibleText = fixture.VisibleText;
        Assert.Contains("Профиль высот", visibleText);
        Assert.Contains("Вариант «Тестовый коридор»", visibleText);
        Assert.Contains("Данные высот недоступны.", visibleText);
        Assert.Contains("м (2D)", fixture.ViewModel.MetricsText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task AlternativeChangeAndPointEditReplaceProfileAndRemoveOldMapCursor()
    {
        using ProfileWindow fixture = new(1100);
        RouteAlternative first = CreateRoute("Высокий берег", true);
        RouteAlternative second = CreateRoute("Низкий берег", false);
        fixture.ShowRoute(first, second);

        Control chart = fixture.Chart;
        Point middle = chart.TranslatePoint(new Point(chart.Bounds.Width / 2, chart.Bounds.Height / 2), fixture.Window)!.Value;
        fixture.Window.MouseMove(middle);
        HeadlessTouch.Present();
        fixture.AssertCursorAt(first.Geometry[1]);
        Assert.True(chart.Focus());
        fixture.Press(PhysicalKey.End);
        fixture.AssertCursorAt(first.Geometry[^1]);
        fixture.Press(PhysicalKey.ArrowLeft);
        fixture.AssertCursorAt(first.Geometry[1]);
        Assert.Contains(fixture.VisibleText, text => text?.Contains("130", StringComparison.Ordinal) == true
            && text.Contains("Высота", StringComparison.Ordinal));

        await fixture.ViewModel.ChooseRoute.Execute(second.Id).ToTask();
        HeadlessTouch.Present();

        Assert.Contains("Вариант «Низкий берег»", fixture.VisibleText);
        Assert.Single(fixture.VisibleText, text => text == "Высокий берег");
        Assert.Contains(fixture.VisibleText, text => text?.Contains("Частичное покрытие", StringComparison.Ordinal) == true);
        Assert.Empty(fixture.CursorLayer.Features);
        Assert.True(fixture.Chart.Focus());
        fixture.Press(PhysicalKey.Home);
        fixture.Press(PhysicalKey.ArrowRight);
        fixture.AssertCursorAt(second.Geometry[1]);
        Assert.Contains(fixture.VisibleText, text => text?.Contains("Высота —", StringComparison.Ordinal) == true);
        await fixture.ViewModel.AddPoint.Execute(TestData.P(50, 160)).ToTask();

        HeadlessTouch.Present();
        Assert.Null(fixture.ViewModel.SelectedRoute);
        Assert.Empty(fixture.CursorLayer.Features);
        Assert.DoesNotContain("Профиль высот", fixture.VisibleText);
    }

    [AvaloniaFact]
    public async Task PhoneWidthFitsTheChartAndVerticalSwipeOverItScrollsWithoutMovingTheCursor()
    {
        using ProfileWindow fixture = new(314);
        RouteAlternative route = CreateRoute("Большой подъём", true);
        fixture.ShowRoute(route);
        Control chart = fixture.Chart;
        Assert.InRange(chart.Bounds.Width, 200, 314);
        ScrollViewer scroll = chart.GetVisualAncestors().OfType<ScrollViewer>().First();
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
        using HeadlessTouch touch = new(fixture.Window);

        Point middle = await touch.TargetAsync(chart);
        touch.Send(RawPointerEventType.TouchBegin, middle);
        touch.Send(RawPointerEventType.TouchEnd, middle);
        fixture.AssertCursorAt(route.Geometry[1]);

        Point right = chart.TranslatePoint(new Point(chart.Bounds.Width - 10, chart.Bounds.Height / 2), fixture.Window)!.Value;
        touch.Send(RawPointerEventType.TouchBegin, middle);
        touch.Send(RawPointerEventType.TouchUpdate, right);
        fixture.AssertCursorAt(route.Geometry[^1]);
        touch.Send(RawPointerEventType.TouchEnd, right);

        Point start = middle - new Point(65, 0);
        double beforeSwipe = scroll.Offset.Y;
        touch.Send(RawPointerEventType.TouchBegin, start);
        touch.Send(RawPointerEventType.TouchUpdate, start - new Point(0, 35));
        touch.Send(RawPointerEventType.TouchUpdate, start - new Point(0, 75));
        touch.Send(RawPointerEventType.TouchEnd, start - new Point(0, 75));
        Assert.True(scroll.Offset.Y > beforeSwipe);
        fixture.AssertCursorAt(route.Geometry[^1]);
    }

    [AvaloniaTheory]
    [InlineData(314)]
    [InlineData(1100)]
    public void SlopeToggleHidesSeriesAndScaleButPreservesSelectedValuesAndMapCursor(double width)
    {
        using ProfileWindow fixture = new(width);
        RouteAlternative route = CreateRoute("Короче", true);
        fixture.ShowRoute(route);
        RouteElevationView view = fixture.ElevationView;
        ToggleSwitch toggle = Assert.IsType<ToggleSwitch>(view.FindControl<ToggleSwitch>("ShowGradeToggle"));
        RouteElevationChart chart = Assert.IsType<RouteElevationChart>(fixture.Chart);
        Assert.Equal("Показать уклон", toggle.Content);
        Assert.False(toggle.IsChecked);
        Assert.Contains("Вариант «Короче»", fixture.VisibleText);
        Assert.DoesNotContain("Уклон, %", fixture.VisibleText);
        AssertSlopeDrawing(chart, visible: false);
        Assert.True(chart.Focus());
        fixture.Press(PhysicalKey.ArrowRight);
        fixture.Press(PhysicalKey.ArrowRight);
        fixture.AssertCursorAt(route.Geometry[1]);
        string cursor = fixture.ViewModel.Elevation!.CursorText;
        Assert.Equal($"{50:N0} м от старта · Высота {130:N1} м · Уклон {10:+0.0;-0.0;0.0} %", cursor);
        Assert.Contains(cursor, fixture.VisibleText);
        string summary = $"Суммарный подъём {30:N0} м · Спуск {20:N0} м\nМин. {100:N0} м · макс. {130:N0} м";
        Assert.Contains(summary, fixture.VisibleText);

        foreach (bool enabled in new[] { true, false })
        {
            toggle.IsChecked = enabled;
            HeadlessTouch.Present();
            Assert.Equal(enabled, fixture.VisibleText.Contains("Уклон, %", StringComparer.Ordinal));
            AssertSlopeDrawing(chart, enabled);
            Assert.Equal(cursor, fixture.ViewModel.Elevation.CursorText);
            Assert.Contains(cursor, fixture.VisibleText);
            Assert.Contains(summary, fixture.VisibleText);
            fixture.AssertCursorAt(route.Geometry[1]);
            Assert.Same(route.ElevationProfile, fixture.ViewModel.Elevation.Profile);
        }
        Point position = toggle.TranslatePoint(default, view)!.Value;
        Assert.InRange(position.X, 0, view.Bounds.Width - toggle.Bounds.Width);
    }

    [AvaloniaTheory]
    [InlineData(30)]
    [InlineData(75)]
    public void MeasurementCaptionUsesDemMetadataAndKeepsSourceAndAttribution(double resolution)
    {
        using ProfileWindow fixture = new(314);
        RouteAlternative route = CreateRoute("Дешевле", true, resolution: resolution);
        fixture.ShowRoute(route);
        Assert.Contains($"Шаг измерения ≈ {resolution:N0} м", fixture.VisibleText);
        Assert.DoesNotContain(fixture.VisibleText, text => text?.Contains("DEM test", StringComparison.Ordinal) == true);
        Assert.Contains("Вариант «Дешевле»", fixture.VisibleText);
        TextBlock caption = Assert.Single(fixture.ElevationView.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == $"Шаг измерения ≈ {resolution:N0} м");
        Assert.Equal("DEM test attribution", ToolTip.GetTip(caption));
        ElevationSource source = fixture.ViewModel.Elevation!.Profile!.Source!;
        Assert.Equal("DEM test", source.Name);
        Assert.Equal("1", source.Version);
        Assert.Equal("EGM2008", source.VerticalDatum);
        Assert.Equal(resolution, source.ResolutionMetres);
        Assert.Equal(new Uri("https://example.com/license"), source.LicenseUri);
    }

    private static void AssertSlopeDrawing(RouteElevationChart chart, bool visible)
    {
        DrawingGroup drawing = new();
        using (DrawingContext context = drawing.Open())
        {
            chart.Render(context);
        }
        Drawing[] items = Descendants(drawing).ToArray();
        Assert.Equal(visible, items.OfType<GeometryDrawing>().Any(item => Equals(item.Pen?.Brush, chart.GradeBrush)));
        Assert.Equal(visible, items.OfType<GlyphRunDrawing>().Any(item => Equals(item.Foreground, chart.GradeBrush)));
        Assert.Contains(items.OfType<GeometryDrawing>(), item => Equals(item.Pen?.Brush, chart.ElevationBrush));

        static IEnumerable<Drawing> Descendants(DrawingGroup group)
        {
            foreach (Drawing child in group.Children)
            {
                yield return child;
                if (child is DrawingGroup nested)
                {
                    foreach (Drawing descendant in Descendants(nested))
                    {
                        yield return descendant;
                    }
                }
            }
        }
    }

    [Fact]
    public void LengthCorrectionUsesOneHorizontalBasisAndReserveUsesTheApplicableLengthOnce()
    {
        RouteAlternative route = CreateRoute("По склону", true);
        double surface = route.ElevationProfile!.SurfaceLengthMetres!.Value;
        string applicable = RouteMetricsFormatter.Format(route, new RoutingOptions(ReserveFraction: 0.2));

        Assert.DoesNotContain("Горизонталь WGS84", applicable, StringComparison.Ordinal);
        Assert.Contains($"По поверхности: {surface:N1} м", applicable, StringComparison.Ordinal);
        Assert.Contains($"+{surface - 100:N1} м", applicable, StringComparison.Ordinal);
        Assert.Contains($"+{(surface - 100) / 100:P1}", applicable, StringComparison.Ordinal);
        Assert.Contains($"Кабель с запасом {0.2:P0}: {surface * 1.2:N0} м (по поверхности)",
            applicable, StringComparison.Ordinal);

        RouteAlternative transition = CreateRoute("По мосту", true, applicable: false);
        string fallback = RouteMetricsFormatter.Format(transition, new RoutingOptions(ReserveFraction: 0.2));
        Assert.DoesNotContain("Горизонталь WGS84", fallback, StringComparison.Ordinal);
        Assert.Contains($"По поверхности: {surface:N1} м", fallback, StringComparison.Ordinal);
        Assert.Contains($"Кабель с запасом {0.2:P0}: {105 * 1.2:N0} м (2D)", fallback, StringComparison.Ordinal);
        Assert.Contains($"Длина для запаса (2D): {105:N1} м", fallback, StringComparison.Ordinal);
        Assert.Contains("Высоты переходов", fallback, StringComparison.Ordinal);
    }

    private static RouteAlternative CreateRoute(string name, bool? complete, bool applicable = true, double resolution = 30)
    {
        int y = complete == false ? 180 : 150;
        GeoCoordinate[] points = [TestData.P(40, y), TestData.P(90, y), TestData.P(140, y)];
        RouteAlternative route = new(Guid.NewGuid(), name, points,
            new RouteMetrics(105, 0, 105, 0, 0, applicable ? 0 : 1, 105, null));
        if (complete is not { } completeCoverage)
        {
            return route;
        }

        ElevationSource source = new("DEM test", "1", "EGM2008", resolution,
            "DEM test attribution", new Uri("https://example.com/license"));
        return route.WithElevationSamples(source,
            [
                new(points[0], 0, 100, completeCoverage ? 60 : null),
                new(points[1], 50, completeCoverage ? 130 : null, completeCoverage ? 10 : null),
                new(points[2], 100, 110, completeCoverage ? -40 : null)
            ],
            100);
    }

    private sealed class ProfileWindow : IDisposable
    {
        private readonly Mapsui.UI.Avalonia.MapControl _map;
        private readonly MapView _view;

        public ProfileWindow(double width)
        {
            ViewModel = TestData.CreateMapViewModel(new NavigationHost(),
                new PlanningSession(TestData.Planner(), TestData.AllowAllValidator),
                new MapAreaLoader(new MemoryData()), new MemorySettings(), new MemoryClipboard());
            _view = new MapView((control, bounds) => new MapsuiMapAdapter(control, bounds)) { ViewModel = ViewModel };
            _map = _view.FindControl<Mapsui.UI.Avalonia.MapControl>("MapControl")!;
            Window = new Window { Content = _view, Width = width, Height = 650 };
            Window.Show();
            HeadlessTouch.Present();
            if (width == 314)
            {
                _view.FindControl<Border>("SheetHandleButton")!.Focus();
                Press(PhysicalKey.Enter);
            }
        }

        public MapViewModel ViewModel { get; }
        public Window Window { get; }

        public RouteElevationView ElevationView => Assert.Single(_view.GetVisualDescendants().OfType<RouteElevationView>());

        public Control Chart => Assert.Single(_view.GetVisualDescendants().OfType<Control>(),
            control => control.Name == "ProfileChart" && control.IsEffectivelyVisible);

        public MemoryLayer CursorLayer => Assert.Single(_map.Map.Layers.OfType<MemoryLayer>(),
            layer => layer.Name == "Курсор профиля высот");

        public IEnumerable<string?> VisibleText => _view.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.IsEffectivelyVisible).Select(text => text.Text);

        public void ShowRoute(RouteAlternative route, params RouteAlternative[] alternatives)
        {
            ViewModel.Alternatives.Add(route);
            foreach (RouteAlternative alternative in alternatives)
            {
                ViewModel.Alternatives.Add(alternative);
            }

            ViewModel.SelectedRoute = route;
            _view.FindControl<TabControl>("ToolTabs")!.SelectedIndex = 1;
            HeadlessTouch.Present();
        }

        public void Press(PhysicalKey key)
        {
            Window.KeyPressQwerty(key, RawInputModifiers.None);
            HeadlessTouch.Present();
        }

        public void AssertCursorAt(GeoCoordinate coordinate)
        {
            GeometryFeature marker = Assert.IsType<GeometryFeature>(Assert.Single(CursorLayer.Features));
            Assert.Equal(MapsuiMapAdapter.Project(coordinate), marker.Geometry!.Coordinate);
            Assert.Null(ViewModel.SelectedRouteVertex);
        }

        public void Dispose()
        {
            Window.Close();
            _map.Dispose();
            ViewModel.Dispose();
        }
    }
}
