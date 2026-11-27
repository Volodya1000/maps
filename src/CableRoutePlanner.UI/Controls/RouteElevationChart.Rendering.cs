using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using CableRoutePlanner.Domain;

namespace CableRoutePlanner.UI.Controls;

public sealed partial class RouteElevationChart
{
    private const double AxisFontSize = 11;
    private const double AxisPadding = 5;
    private const double DotRadius = 2.5;
    private const double CursorRadius = 3.5;

    private ChartPlot? _plot;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (GetPlot() is not { } plot)
        {
            return;
        }

        // A transparent hit area keeps input working in gaps between series.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        DrawAxes(context, plot);
        using (context.PushClip(plot.Area.Inflate(CursorRadius)))
        {
            if (plot.Elevation.Area is { } area)
            {
                context.DrawGeometry(ElevationFill, null, area);
            }
            DrawSeries(context, plot.Elevation, ElevationBrush);
            if (ShowGrade)
            {
                DrawSeries(context, plot.Grade, GradeBrush);
            }
            if (Presentation?.CursorSample is { } sample)
            {
                DrawCursor(context, plot, sample);
            }
        }
    }

    private void DrawAxes(DrawingContext context, ChartPlot plot)
    {
        Pen grid = new(GridBrush);
        for (int i = 0; i < plot.ElevationTicks.Count; i++)
        {
            double y = plot.Area.Top + (plot.Area.Height * i / (plot.ElevationTicks.Count - 1));
            context.DrawLine(grid, new Point(plot.Area.Left, y), new Point(plot.Area.Right, y));
            FormattedText elevation = plot.ElevationTicks[i];
            context.DrawText(elevation, new Point(plot.Area.Left - AxisPadding - elevation.Width, y - elevation.Height / 2));
            if (ShowGrade)
            {
                FormattedText grade = plot.GradeTicks[i];
                context.DrawText(grade, new Point(plot.Area.Right + AxisPadding, y - grade.Height / 2));
            }
        }

        double yLabel = plot.Area.Bottom + AxisPadding;
        context.DrawText(plot.StartLabel, new Point(plot.Area.Left, yLabel));
        context.DrawText(plot.EndLabel, new Point(plot.Area.Right - plot.EndLabel.Width, yLabel));
        double centre = plot.Area.Center.X - plot.DistanceLabel.Width / 2;
        if (centre >= plot.Area.Left + plot.StartLabel.Width + AxisPadding
            && centre + plot.DistanceLabel.Width + AxisPadding <= plot.Area.Right - plot.EndLabel.Width)
        {
            context.DrawText(plot.DistanceLabel, new Point(centre, yLabel));
        }
    }

    private static void DrawSeries(DrawingContext context, PlotSeries series, IBrush stroke)
    {
        context.DrawGeometry(null, new Pen(stroke, 1.75), series.Line);
        foreach (Point point in series.IsolatedPoints)
        {
            context.DrawEllipse(stroke, null, point, DotRadius, DotRadius);
        }
    }

    private void DrawCursor(DrawingContext context, ChartPlot plot, RouteElevationSample sample)
    {
        double x = GetX(plot.Area, plot.Profile.HorizontalLengthMetres, sample.DistanceMetres);
        context.DrawLine(new Pen(CursorBrush, 1.5), new Point(x, plot.Area.Top), new Point(x, plot.Area.Bottom));
        if (sample.ElevationMetres is { } elevation)
        {
            double y = plot.ElevationRange.Y(plot.Area, elevation);
            context.DrawEllipse(CursorBrush, new Pen(Brushes.White, 1.5), new Point(x, y), CursorRadius, CursorRadius);
        }
    }

    private ChartPlot? GetPlot()
    {
        if (Presentation is not { HasChart: true, Profile: { } profile } || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return null;
        }
        _plot = _plot is { } plot && plot.Size == Bounds.Size ? plot : BuildPlot(profile, Bounds.Size);
        return _plot;
    }

    private ChartPlot? BuildPlot(RouteElevationProfile profile, Size size)
    {
        double minimumElevation = double.PositiveInfinity;
        double maximumElevation = double.NegativeInfinity;
        double maximumGrade = 1;
        foreach (RouteElevationSample sample in profile.Samples)
        {
            if (sample.ElevationMetres is { } elevation)
            {
                minimumElevation = Math.Min(minimumElevation, elevation);
                maximumElevation = Math.Max(maximumElevation, elevation);
            }
            if (sample.GradePercent is { } grade)
            {
                maximumGrade = Math.Max(maximumGrade, Math.Abs(grade));
            }
        }

        AxisRange elevationRange = new(Math.Floor(minimumElevation - 0.5), Math.Ceiling(maximumElevation + 0.5));
        double gradeBound = Math.Ceiling(maximumGrade);
        AxisRange gradeRange = new(-gradeBound, gradeBound);
        FormattedText[] elevationTicks = FormatTicks(elevationRange, ElevationBrush);
        FormattedText[] gradeTicks = FormatTicks(gradeRange, GradeBrush);
        FormattedText start = FormatLabel("0 м", AxisBrush);
        FormattedText end = FormatLabel(profile.HorizontalLengthMetres >= 1000
            ? $"{profile.HorizontalLengthMetres / 1000:N2} км" : $"{profile.HorizontalLengthMetres:N0} м", AxisBrush);
        double top = elevationTicks[0].Height / 2 + 1;
        double left = elevationTicks.Max(tick => tick.Width) + (AxisPadding * 2);
        double right = ShowGrade ? gradeTicks.Max(tick => tick.Width) + (AxisPadding * 2) : AxisPadding;
        double width = size.Width - left - right;
        double height = size.Height - top - (start.Height + AxisPadding);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        Rect area = new(left, top, width, height);
        return new ChartPlot(profile, size, area, elevationRange, elevationTicks, gradeTicks, start, end,
            FormatLabel("Расстояние", AxisBrush),
            BuildSeries(profile, area, elevationRange, sample => sample.ElevationMetres, fill: true),
            BuildSeries(profile, area, gradeRange, sample => sample.GradePercent, fill: false));
    }

    private static FormattedText[] FormatTicks(AxisRange range, IBrush brush) =>
        [FormatTick(range.Maximum, brush), FormatTick(range.Midpoint, brush), FormatTick(range.Minimum, brush)];

    private static FormattedText FormatTick(double value, IBrush brush) =>
        FormatLabel(value.ToString("G5", CultureInfo.CurrentCulture), brush);

    private static FormattedText FormatLabel(string value, IBrush brush) =>
        new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, AxisFontSize, brush);

    private static PlotSeries BuildSeries(
        RouteElevationProfile profile, Rect area, AxisRange range, Func<RouteElevationSample, double?> selectValue, bool fill)
    {
        StreamGeometry line = new();
        StreamGeometry? fillArea = fill ? new StreamGeometry() : null;
        using StreamGeometryContext linePath = line.Open();
        using StreamGeometryContext? fillPath = fillArea?.Open();
        List<Point> isolated = [];
        IReadOnlyList<RouteElevationSample> samples = profile.Samples;
        int index = 0;
        while (index < samples.Count)
        {
            RouteElevationSample firstSample = samples[index++];
            if (selectValue(firstSample) is not { } value)
            {
                continue;
            }

            Point first = ToPoint(firstSample, value);
            if (index == samples.Count || !selectValue(samples[index]).HasValue)
            {
                isolated.Add(first);
                continue;
            }

            // End each figure at a missing value, including its blue fill.
            linePath.BeginFigure(first, isFilled: false);
            fillPath?.BeginFigure(new Point(first.X, area.Bottom), isFilled: true);
            fillPath?.LineTo(first);
            Point last = first;
            while (index < samples.Count && selectValue(samples[index]) is { } next)
            {
                last = ToPoint(samples[index++], next);
                linePath.LineTo(last);
                fillPath?.LineTo(last);
            }

            linePath.EndFigure(isClosed: false);
            fillPath?.LineTo(new Point(last.X, area.Bottom));
            fillPath?.EndFigure(isClosed: true);
        }
        return new PlotSeries(line, fillArea, isolated);

        Point ToPoint(RouteElevationSample sample, double value) =>
            new(GetX(area, profile.HorizontalLengthMetres, sample.DistanceMetres), range.Y(area, value));
    }

    private static double GetX(Rect area, double horizontalLength, double distance) =>
        horizontalLength > 0 ? area.Left + (area.Width * (distance / horizontalLength)) : area.Center.X;

    private sealed record PlotSeries(StreamGeometry Line, StreamGeometry? Area, IReadOnlyList<Point> IsolatedPoints);

    private sealed record ChartPlot(
        RouteElevationProfile Profile,
        Size Size,
        Rect Area,
        AxisRange ElevationRange,
        IReadOnlyList<FormattedText> ElevationTicks,
        IReadOnlyList<FormattedText> GradeTicks,
        FormattedText StartLabel,
        FormattedText EndLabel,
        FormattedText DistanceLabel,
        PlotSeries Elevation,
        PlotSeries Grade);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct AxisRange(double Minimum, double Maximum)
    {
        public double Midpoint => Minimum / 2 + Maximum / 2;

        public double Y(Rect area, double value) => area.Bottom - (area.Height * ((value - Minimum) / (Maximum - Minimum)));
    }
}
