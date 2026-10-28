using CableRoutePlanner.Domain;
using Mapsui;
using Mapsui.Styles;

namespace CableRoutePlanner.UI.MapAdapters;

internal static class MapsuiStyleFactory
{
    public static int DrawingOrder(TerrainKind kind) => kind switch
    {
        TerrainKind.Forest or TerrainKind.Field or TerrainKind.Marsh => 0,
        TerrainKind.Lake or TerrainKind.River => 1,
        TerrainKind.Building => 2,
        TerrainKind.Clearing => 3,
        TerrainKind.Road => 4,
        TerrainKind.BridgeCandidate or TerrainKind.ApprovedCrossing => 5,
        TerrainKind.Settlement => 6,
        _ => 3
    };

    public static IReadOnlyList<IStyle> ForTerrain(TerrainKind kind)
    {
        Color color = kind switch
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

        var main = new VectorStyle
        {
            Fill = new Brush(color),
            Outline = new Pen(new Color(182, 187, 174), 0.5),
            Line = new Pen(
                kind == TerrainKind.Road ? new Color(195, 182, 155) : color,
                kind == TerrainKind.Road ? 2 : 3)
        };

        if (kind is not (TerrainKind.Road or TerrainKind.BridgeCandidate or TerrainKind.ApprovedCrossing))
        {
            return [main];
        }

        Color casing = kind == TerrainKind.Road
            ? new Color(150, 139, 120)
            : new Color(91, 81, 69);
        return
        [
            new VectorStyle
            {
                Line = new Pen(casing, kind == TerrainKind.Road ? 4 : 5)
            },
            main
        ];
    }
}
