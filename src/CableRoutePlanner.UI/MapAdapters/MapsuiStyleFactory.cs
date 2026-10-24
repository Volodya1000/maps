using CableRoutePlanner.Domain;
using Mapsui;
using Mapsui.Styles;

namespace CableRoutePlanner.UI.MapAdapters;

internal static class MapsuiStyleFactory
{
    public static VectorStyle ForTerrain(TerrainKind kind)
    {
        var color = kind switch
        {
            TerrainKind.Forest => new Color(188, 214, 176),
            TerrainKind.Field => new Color(231, 224, 179),
            TerrainKind.Lake or TerrainKind.River => new Color(151, 196, 218),
            TerrainKind.Marsh => new Color(141, 179, 168),
            TerrainKind.Building => new Color(201, 197, 188),
            TerrainKind.BridgeCandidate => new Color(168, 143, 113),
            TerrainKind.ApprovedCrossing => new Color(230, 171, 53),
            _ => Color.White
        };

        return new VectorStyle
        {
            Fill = new Brush(color),
            Outline = new Pen(new Color(182, 187, 174), 0.5),
            Line = new Pen(
                kind == TerrainKind.Road ? new Color(195, 182, 155) : color,
                kind == TerrainKind.Road ? 2 : 3)
        };
    }
}
