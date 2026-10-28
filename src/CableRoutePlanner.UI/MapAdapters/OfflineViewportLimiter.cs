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
        // Keep the camera centre inside the offline package so a drag cannot leave
        // almost the entire screen empty. Edge locations can still reach the centre.
        double radians = viewport.Rotation * Math.PI / 180;
        double cosine = Math.Abs(Math.Cos(radians));
        double sine = Math.Abs(Math.Sin(radians));
        double projectedWidth = extent.Width * cosine + extent.Height * sine;
        double projectedHeight = extent.Width * sine + extent.Height * cosine;
        double maximum = Math.Max(projectedWidth / viewport.Width, projectedHeight / viewport.Height);
        double minimum = Math.Min(zoomBounds?.Min ?? 0, maximum);
        double resolution = Math.Clamp(viewport.Resolution, minimum, maximum);
        return viewport with
        {
            Resolution = resolution,
            CenterX = Math.Clamp(viewport.CenterX, extent.MinX, extent.MaxX),
            CenterY = Math.Clamp(viewport.CenterY, extent.MinY, extent.MaxY)
        };
    }
}
