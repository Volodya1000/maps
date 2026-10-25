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

        // At the overview, keep unavoidable aspect-ratio margins centred. At
        // closer zoom levels keep every visible edge within the offline area.
        var maximum = Math.Max(extent.Width / viewport.Width, extent.Height / viewport.Height);
        var minimum = Math.Min(zoomBounds?.Min ?? 0, maximum);
        var resolution = Math.Clamp(viewport.Resolution, minimum, maximum);
        var horizontal = Math.Min(extent.Width / 2, viewport.Width * resolution / 2);
        var vertical = Math.Min(extent.Height / 2, viewport.Height * resolution / 2);
        return viewport with
        {
            Resolution = resolution,
            CenterX = Math.Clamp(viewport.CenterX, extent.MinX + horizontal, extent.MaxX - horizontal),
            CenterY = Math.Clamp(viewport.CenterY, extent.MinY + vertical, extent.MaxY - vertical)
        };
    }
}
