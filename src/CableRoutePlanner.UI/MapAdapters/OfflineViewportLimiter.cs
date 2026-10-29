using Mapsui;
using Mapsui.Limiting;

namespace CableRoutePlanner.UI.MapAdapters;

public sealed class OfflineViewportLimiter(MRect extent) : IViewportLimiter
{
    public Viewport Limit(Viewport viewport, MRect? panBounds, MMinMax? zoomBounds)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            return viewport;
        }

        // Rotation enlarges the axis-aligned world footprint of the screen.
        // Keep the visible footprint inside the offline package where it fits.
        // If overview aspect ratios leave margins, centre the package in that axis.
        double radians = viewport.Rotation * Math.PI / 180;
        double cosine = Math.Abs(Math.Cos(radians));
        double sine = Math.Abs(Math.Sin(radians));
        double projectedWidth = extent.Width * cosine + extent.Height * sine;
        double projectedHeight = extent.Width * sine + extent.Height * cosine;
        double maximum = Math.Max(projectedWidth / viewport.Width, projectedHeight / viewport.Height);
        double minimum = Math.Min(zoomBounds?.Min ?? 0, maximum);
        double resolution = Math.Clamp(viewport.Resolution, minimum, maximum);
        double halfWidth = Math.Min(extent.Width / 2,
            (viewport.Width * cosine + viewport.Height * sine) * resolution / 2);
        double halfHeight = Math.Min(extent.Height / 2,
            (viewport.Width * sine + viewport.Height * cosine) * resolution / 2);
        return viewport with
        {
            Resolution = resolution,
            CenterX = LimitCenter(viewport.CenterX, extent.MinX, extent.MaxX, halfWidth),
            CenterY = LimitCenter(viewport.CenterY, extent.MinY, extent.MaxY, halfHeight)
        };
    }

    private static double LimitCenter(double requested, double minimum, double maximum, double halfSpan)
    {
        double lower = minimum + halfSpan;
        double upper = maximum - halfSpan;
        // At a full-package overview, large Mercator coordinates can round the
        // two limits in opposite directions. A collapsed range means centring.
        return lower >= upper ? minimum + (maximum - minimum) / 2 : Math.Clamp(requested, lower, upper);
    }
}
