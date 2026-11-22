using Mapsui;
using Mapsui.Layers;
using Mapsui.Rendering.Skia;
using Mapsui.Styles;

namespace CableRoutePlanner.UI.MapAdapters;

// Render the shared label style once; Mapsui's symbol renderer handles map
// rotation and world anchoring on both Windows and Android.
internal static class GasLabelSymbol
{
    private static readonly Lazy<int> RegisteredBitmap = new(CreateBitmap);
    public static int BitmapId => RegisteredBitmap.Value;

    private static int CreateBitmap()
    {
        PointFeature label = new(new MPoint(0, 0));
        label.Styles.Add(MapsuiStyleFactory.MapLabel("ГАЗ", Color.White,
            new Brush(new Color(0, 112, 126)), new Offset(0, 0), double.MaxValue));
        using MemoryLayer layer = new() { Features = [label], Style = null };
        using MemoryStream png = new MapRenderer().RenderToBitmapStream(
            new Viewport(0, 0, 1, 0, 48, 24), [layer], Color.Transparent);
        // BitmapRegistry owns the retained stream, as for the underpass symbol.
        return BitmapRegistry.Instance.Register(new MemoryStream(png.ToArray()));
    }
}
