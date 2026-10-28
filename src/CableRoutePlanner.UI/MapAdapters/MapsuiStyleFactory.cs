using CableRoutePlanner.Domain;
using Mapsui;
using Mapsui.Styles;

namespace CableRoutePlanner.UI.MapAdapters;

internal static class MapsuiStyleFactory
{
    private static readonly Color MapBackground = new(244, 242, 233);

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

    public static IReadOnlyList<IStyle> ForTerrain(TerrainKind kind, double metresPerPixel = 0)
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

        double roadWidth = RoadWidth(metresPerPixel);
        var main = new VectorStyle
        {
            Fill = new Brush(color),
            Outline = new Pen(new Color(182, 187, 174), 0.5),
            Line = new Pen(
                kind == TerrainKind.Road ? new Color(195, 182, 155) : color,
                kind == TerrainKind.Road ? roadWidth
                    : kind is TerrainKind.BridgeCandidate or TerrainKind.ApprovedCrossing ? Math.Max(1, roadWidth)
                    : 3)
            {
                PenStrokeCap = PenStrokeCap.Round
            }
        };

        if (kind is not (TerrainKind.BridgeCandidate or TerrainKind.ApprovedCrossing))
        {
            return [main];
        }

        return
        [
            new VectorStyle
            {
                // The light casing masks the water below the OSM bridge centreline.
                // Square caps extend half a stroke beyond the tagged span and make
                // small shoreline mismatches less prominent without changing geometry.
                Line = new Pen(MapBackground, Math.Max(3, roadWidth + 2))
                {
                    PenStrokeCap = PenStrokeCap.Square
                }
            },
            main
        ];
    }

    private static double RoadWidth(double metresPerPixel) => metresPerPixel switch
    {
        > 20 => 0.65,
        > 5 => 0.85,
        > 2 => 1.1,
        > 0 => 1.5,
        _ => 1.5
    };
}
