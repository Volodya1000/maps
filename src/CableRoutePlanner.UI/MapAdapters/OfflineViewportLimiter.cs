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

        // Rotation enlarges the axis-aligned world footprint of the screen. Fit the
        // complete package at overview, then clamp its visible footprint on closer zooms.
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
        var horizontal = Math.Min(extent.Width / 2, visibleHalfWidth);
        var vertical = Math.Min(extent.Height / 2, visibleHalfHeight);
        return viewport with
        {
            Resolution = resolution,
            CenterX = Math.Clamp(viewport.CenterX, extent.MinX + horizontal, extent.MaxX - horizontal),
            CenterY = Math.Clamp(viewport.CenterY, extent.MinY + vertical, extent.MaxY - vertical)
        };
    }
}
