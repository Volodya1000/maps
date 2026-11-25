using CableRoutePlanner.Domain;
using Mapsui;
using Mapsui.Styles;

namespace CableRoutePlanner.UI.MapAdapters;

internal static class MapsuiStyleFactory
{
    public const double CrossingSymbolMaxResolution = 5;
    public const double UnderpassLabelMaxResolution = 2;
    public const double UnderpassLineMaxResolution = 10;
    internal const double DetailedRailwayMaxResolution = 2;
    private const double DetailedBridgeMaxResolution = 10;
    public static Color MapBackground { get; } = new(244, 242, 233);

    public static int DrawingOrder(TerrainKind kind) => kind switch
    {
        TerrainKind.Forest or TerrainKind.Field or TerrainKind.Marsh => 0,
        TerrainKind.Lake or TerrainKind.River => 1,
        TerrainKind.Building => 2,
        TerrainKind.Clearing => 3,
        TerrainKind.Road => 4,
        TerrainKind.RailwayTunnel => 4,
        TerrainKind.Railway => 5,
        TerrainKind.BridgeCandidate or TerrainKind.ApprovedCrossing => 6,
        TerrainKind.RailwayBridge => 7,
        TerrainKind.Settlement => 8,
        TerrainKind.GasPipelineCrossingCandidate => 7,
        TerrainKind.PedestrianUnderpassCandidate => 8,
        _ => 3
    };

    public static IReadOnlyList<IStyle> ForTerrain(TerrainKind kind, double metresPerPixel = 0)
    {
        if (kind is TerrainKind.Railway or TerrainKind.RailwayBridge or TerrainKind.RailwayTunnel)
        {
            bool detailed = metresPerPixel <= DetailedRailwayMaxResolution;
            double width = detailed ? 2.8 : metresPerPixel > 20 ? 1.4 : 1.6;
            Color bedColor = detailed ? new Color(85, 85, 85) : new Color(145, 145, 145);
            VectorStyle bed = new()
            {
                Fill = null,
                Outline = null,
                Line = new Pen(bedColor, width)
                {
                    PenStrokeCap = PenStrokeCap.Butt,
                    StrokeJoin = StrokeJoin.Round,
                    PenStyle = detailed && kind == TerrainKind.RailwayTunnel ? PenStyle.ShortDash : PenStyle.Solid
                }
            };
            if (!detailed || kind == TerrainKind.RailwayTunnel)
            {
                return [bed];
            }
            List<IStyle> railwayStyles = [];
            if (kind == TerrainKind.RailwayBridge)
            {
                railwayStyles.Add(new VectorStyle
                {
                    Line = new Pen(MapBackground, width + 1) { PenStrokeCap = PenStrokeCap.Butt }
                });
            }
            railwayStyles.Add(bed);
            railwayStyles.Add(new VectorStyle
            {
                Line = new Pen(Color.White, 1.4)
                {
                    // Mapsui scales dash lengths by the stroke width: these
                    // inserts and gaps are 8.4 screen pixels, even when zoomed.
                    PenStyle = PenStyle.UserDefined,
                    DashArray = [6, 6],
                    PenStrokeCap = PenStrokeCap.Butt
                }
            });
            return railwayStyles;
        }
        if (kind == TerrainKind.GasPipelineCrossingCandidate)
        {
            return [
                new VectorStyle { Line = new Pen(Color.White, 7) },
                new VectorStyle { Line = new Pen(new Color(0, 143, 157), 4) }
            ];
        }
        if (kind == TerrainKind.PedestrianUnderpassCandidate)
        {
            return
            [
                new VectorStyle { Line = new Pen(Color.White, 6), MaxVisible = UnderpassLineMaxResolution },
                new VectorStyle
                {
                    Line = new Pen(new Color(26, 111, 210), 3)
                    {
                        PenStyle = PenStyle.ShortDash,
                        PenStrokeCap = PenStrokeCap.Round
                    },
                    MaxVisible = UnderpassLineMaxResolution
                },
            ];
        }
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
        bool detailedBridge = kind == TerrainKind.BridgeCandidate && metresPerPixel <= DetailedBridgeMaxResolution;
        var main = new VectorStyle
        {
            Fill = new Brush(color),
            Outline = new Pen(new Color(182, 187, 174), 0.5),
            Line = new Pen(
                kind == TerrainKind.Road ? new Color(195, 182, 155)
                    : detailedBridge ? new Color(77, 91, 99) : color,
                kind == TerrainKind.Road ? roadWidth
                    : detailedBridge ? 3
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

        List<IStyle> bridgeStyles =
        [
            new VectorStyle
            {
                // The light casing masks the water below the OSM bridge centreline.
                // Square caps extend half a stroke beyond the tagged span and make
                // small shoreline mismatches less prominent without changing geometry.
                Line = new Pen(MapBackground, detailedBridge ? 6 : Math.Max(3, roadWidth + 2))
                {
                    PenStrokeCap = PenStrokeCap.Square
                }
            },
            main
        ];
        return bridgeStyles;
    }

    // Point labels are drawn directly by Mapsui in screen coordinates. They
    // must never be included in an OfflineRasterLayer source feature.
    public static LabelStyle MapLabel(string text, Color color, Brush background,
        Offset offset, double maxVisible) => new()
        {
            Text = text,
            Font = new Font { FontFamily = "DejaVu Sans", Size = 12 },
            ForeColor = color,
            BackColor = background,
            Offset = offset,
            MaxVisible = maxVisible,
            CollisionDetection = true,
            HorizontalAlignment = LabelStyle.HorizontalAlignmentEnum.Center,
            VerticalAlignment = LabelStyle.VerticalAlignmentEnum.Center
        };

    private static double RoadWidth(double metresPerPixel) => metresPerPixel switch
    {
        > 20 => 0.65,
        > 5 => 0.85,
        > 2 => 1.1,
        > 0 => 1.5,
        _ => 1.5
    };
}
