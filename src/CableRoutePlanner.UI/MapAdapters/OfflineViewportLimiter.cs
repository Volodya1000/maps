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
        var radians = viewport.Rotation * Math.PI / 180;
        var cosine = Math.Abs(Math.Cos(radians));
        var sine = Math.Abs(Math.Sin(radians));
        var projectedWidth = extent.Width * cosine + extent.Height * sine;
        var projectedHeight = extent.Width * sine + extent.Height * cosine;
        var maximum = Math.Max(projectedWidth / viewport.Width, projectedHeight / viewport.Height);
        var minimum = Math.Min(zoomBounds?.Min ?? 0, maximum);
        var resolution = Math.Clamp(viewport.Resolution, minimum, maximum);
        var visibleHalfWidth = (viewport.Width * cosine + viewport.Height * sine) * resolution / 2;
        var visibleHalfHeight = (viewport.Width * sine + viewport.Height * cosine) * resolution / 2;
        var requiredVisibleWidth = Math.Min(
            Math.Min(extent.Width, visibleHalfWidth * 2),
            resolution * MinimumVisibleMapPixels);
        var requiredVisibleHeight = Math.Min(
            Math.Min(extent.Height, visibleHalfHeight * 2),
            resolution * MinimumVisibleMapPixels);
        var minimumCenterX = extent.MinX - visibleHalfWidth + requiredVisibleWidth / 2;
        var maximumCenterX = extent.MaxX + visibleHalfWidth - requiredVisibleWidth / 2;
        var minimumCenterY = extent.MinY - visibleHalfHeight + requiredVisibleHeight / 2;
        var maximumCenterY = extent.MaxY + visibleHalfHeight - requiredVisibleHeight / 2;
        return viewport with
        {
            Resolution = resolution,
            CenterX = Math.Clamp(viewport.CenterX, minimumCenterX, maximumCenterX),
            CenterY = Math.Clamp(viewport.CenterY, minimumCenterY, maximumCenterY)
        };
    }
}
