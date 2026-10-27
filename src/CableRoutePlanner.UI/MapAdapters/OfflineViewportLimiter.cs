using Mapsui;
using Mapsui.Limiting;

namespace CableRoutePlanner.UI.MapAdapters;

public sealed class OfflineViewportLimiter(MRect extent) : IViewportLimiter
{
    // Keep a clearly visible strip of the package while allowing an edge location to
    // be brought near the centre of the screen.
    private const double MinimumVisibleMapPixels = 48;

    public Viewport Limit(Viewport viewport, MRect? panBounds, MMinMax? zoomBounds)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            return viewport;
        }

        // Rotation enlarges the axis-aligned world footprint of the screen. At overview
        // the complete package fits. At closer zooms, overscroll is allowed until only a
        // small screen-space strip remains: this makes edge locations reachable while
        // never letting an entirely empty map replace the offline package.
        double radians = viewport.Rotation * Math.PI / 180;
        double cosine = Math.Abs(Math.Cos(radians));
        double sine = Math.Abs(Math.Sin(radians));
        double projectedWidth = extent.Width * cosine + extent.Height * sine;
        double projectedHeight = extent.Width * sine + extent.Height * cosine;
        double maximum = Math.Max(projectedWidth / viewport.Width, projectedHeight / viewport.Height);
        double minimum = Math.Min(zoomBounds?.Min ?? 0, maximum);
        double resolution = Math.Clamp(viewport.Resolution, minimum, maximum);
        double visibleHalfWidth = (viewport.Width * cosine + viewport.Height * sine) * resolution / 2;
        double visibleHalfHeight = (viewport.Width * sine + viewport.Height * cosine) * resolution / 2;
        double requiredVisibleWidth = Math.Min(
            Math.Min(extent.Width, visibleHalfWidth * 2),
            resolution * MinimumVisibleMapPixels);
        double requiredVisibleHeight = Math.Min(
            Math.Min(extent.Height, visibleHalfHeight * 2),
            resolution * MinimumVisibleMapPixels);
        double minimumCenterX = extent.MinX - visibleHalfWidth + requiredVisibleWidth / 2;
        double maximumCenterX = extent.MaxX + visibleHalfWidth - requiredVisibleWidth / 2;
        double minimumCenterY = extent.MinY - visibleHalfHeight + requiredVisibleHeight / 2;
        double maximumCenterY = extent.MaxY + visibleHalfHeight - requiredVisibleHeight / 2;
        return viewport with
        {
            Resolution = resolution,
            CenterX = Math.Clamp(viewport.CenterX, minimumCenterX, maximumCenterX),
            CenterY = Math.Clamp(viewport.CenterY, minimumCenterY, maximumCenterY)
        };
    }
}
