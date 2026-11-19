using Mapsui;
using Mapsui.Layers;
using Mapsui.Rendering.Skia;
using Mapsui.Styles;
using SkiaSharp;
using System.Collections.Concurrent;

namespace CableRoutePlanner.UI.MapAdapters;

// A vector layer shared by all geographic point annotations. Selection is
// evaluated at render resolution, so zooming can restore a suppressed label
// without refetching data or mutating the provider's cached features.
internal sealed class MapAnnotationLayer : Layer
{
    private const double BackgroundPaddingPixels = 3;
    private const double LabelSpacingPixels = 2;
    internal const string OrderField = "map_annotation_order";
    internal const string LabelKeyField = "map_label_key";
    internal const string CacheAnchorField = "map_label_cache_anchor";
    private readonly ConcurrentDictionary<string, MPoint> _anchors = new(StringComparer.Ordinal);
    private IFeature[]? _features;

    public MapAnnotationLayer() : base("Подписи и знаки карты")
    {
        Style = null;
    }

    public override IEnumerable<IFeature> GetFeatures(MRect box, double resolution) =>
        Layout(StableLabels(_features ?? base.GetFeatures(box, resolution)), resolution);

    internal void SetFeatures(IFeature[] features)
    {
        _anchors.Clear();
        _features = features;
        DataHasChanged();
    }

    private IEnumerable<IFeature> StableLabels(IEnumerable<IFeature> features)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (IFeature feature in features.OrderBy(feature => feature[OrderField] is int order ? order : int.MaxValue))
        {
            if (feature.Extent is not { } extent)
            {
                continue;
            }
            PointFeature symbols = new(extent.Centroid)
            {
                Styles = feature.Styles.Where(style => style is not LabelStyle).ToArray()
            };
            if (symbols.Styles.Count > 0)
            {
                yield return symbols;
            }
            foreach (LabelStyle label in feature.Styles.OfType<LabelStyle>())
            {
                string? key = feature[LabelKeyField] as string;
                if (key is null || !keys.Add(key))
                {
                    continue;
                }
                // An expanding/rotating fetch may return another entrance first.
                // Retain the network's original world anchor for this map session.
                MPoint anchor = feature[CacheAnchorField] is true
                    ? _anchors.GetOrAdd(key, extent.Centroid)
                    : extent.Centroid;
                PointFeature point = new(anchor) { Styles = new IStyle[] { label } };
                foreach (string field in feature.Fields)
                {
                    point[field] = feature[field];
                }
                yield return point;
            }
        }
    }

    internal static IEnumerable<IFeature> Layout(IEnumerable<IFeature> features, double resolution)
    {
        List<(MPoint Center, double Radius)> occupied = [];
        foreach (IFeature feature in features)
        {
            if (feature.Extent is not { } extent)
            {
                continue;
            }
            MPoint anchor = extent.Centroid;
            List<IStyle> styles = [];
            foreach (IStyle style in feature.Styles)
            {
                if (style is not LabelStyle label || !label.Enabled || !label.CollisionDetection
                    || resolution < label.MinVisible || resolution > label.MaxVisible)
                {
                    styles.Add(style);
                    continue;
                }
                double radius = RadiusPixels(label, feature);
                if (occupied.Any(other => Math.Sqrt(Math.Pow(anchor.X - other.Center.X, 2)
                    + Math.Pow(anchor.Y - other.Center.Y, 2)) < (radius + other.Radius) * resolution))
                {
                    continue;
                }
                occupied.Add((anchor, radius));
                styles.Add(style);
            }
            // Keep cached labels available for the next frame and zoom level.
            PointFeature copy = new(anchor) { Styles = styles };
            foreach (string field in feature.Fields)
            {
                copy[field] = feature[field];
            }
            yield return copy;
        }
    }

    private static double RadiusPixels(LabelStyle label, IFeature feature)
    {
        using SKTypeface typeface = LabelStyleRenderer.CreateTypeFace(label.Font);
        using SKPaint paint = new() { Typeface = typeface, TextSize = (float)label.Font.Size };
        SKRect bounds = new();
        paint.MeasureText(label.GetLabelText(feature), ref bounds);
        double offsetX = label.Offset.IsRelative ? bounds.Width * label.Offset.X : label.Offset.X;
        double offsetY = label.Offset.IsRelative ? bounds.Height * label.Offset.Y : label.Offset.Y;
        double padding = BackgroundPaddingPixels + LabelSpacingPixels + (label.Halo?.Width ?? 0);
        double halfWidth = bounds.Width / 2 + Math.Abs(offsetX) + padding;
        double halfHeight = bounds.Height / 2 + Math.Abs(offsetY) + padding;
        // Circumscribed circles prevent overlap at every angle without changing
        // priority as the map rotates; close labels may be hidden conservatively.
        return Math.Sqrt(halfWidth * halfWidth + halfHeight * halfHeight);
    }
}
