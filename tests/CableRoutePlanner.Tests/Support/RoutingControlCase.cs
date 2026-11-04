using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Persistence;

namespace CableRoutePlanner.Tests;

/// <summary>Shared input recipes for the committed controls and independent successful-route checks.</summary>
internal sealed record RoutingControlCase(IGeodataRepository Data, GeoCoordinate[] Points, RoutingOptions Options)
{
    public static RoutingControlCase Create(string name)
    {
        List<MapFeature> features = [];
        GeoCoordinate[] points = [TestData.P(50, 150), TestData.P(280, 150)];
        RoutingOptions options = TestData.Options;
        switch (name)
        {
            case "minsk-direct":
            case "minsk-bridge":
            case "minsk-waypoints":
                GeoCoordinate start = new(53.9409, 27.4977);
                GeoCoordinate approach = new(53.9411157, 27.49791875);
                GeoCoordinate end = new(53.9424, 27.4991);
                return new(new SqliteGeodataRepository(TestData.RealData), name switch
                {
                    "minsk-direct" => [start, approach],
                    "minsk-waypoints" => [start, approach, end],
                    _ => [start, end]
                }, new RoutingOptions());
            case "empty":
                break;
            case "building":
            case "marsh":
            case "lake":
                TerrainKind kind = name switch
                {
                    "building" => TerrainKind.Building,
                    "marsh" => TerrainKind.Marsh,
                    _ => TerrainKind.Lake
                };
                features.Add(TestData.Polygon(1, kind, 120, 80, 180, 240));
                break;
            case "approved-river":
            case "curved-bridge":
                features.Add(TestData.Polygon(1, TerrainKind.River, 140, -100, 180, 1000));
                features.Add(name == "approved-river"
                    ? TestData.Polygon(2, TerrainKind.ApprovedCrossing, 130, 140, 190, 160, "surveyed-1")
                    : TestData.Line(2, TerrainKind.BridgeCandidate, (110, 150), (150, 170), (190, 150)));
                break;
            case "road-bridge":
            case "unmapped-road":
                features.Add(TestData.Line(1, TerrainKind.Road, (150, -5000), (150, 5000)));
                if (name == "road-bridge")
                { features.Add(TestData.Line(2, TerrainKind.BridgeCandidate, (130, 150), (170, 150))); }
                break;
            case "gas-off":
            case "gas-on":
                features.Add(TestData.Line(1, TerrainKind.Road, (-100, 150), (700, 150)));
                features.Add(TestData.Line(2, TerrainKind.GasPipelineCrossingCandidate,
                    (148, 138), (160, 138), (160, 150), (160, 162), (172, 162)));
                points = [TestData.P(40, 100), TestData.P(280, 200)];
                options = options with { PreferOverheadGasCrossings = name == "gas-on", CellSizeMetres = 80 };
                break;
            case "terrain-profiles":
                features.Add(TestData.Polygon(1, TerrainKind.Forest, 100, 60, 220, 240));
                features.Add(TestData.Polygon(2, TerrainKind.Field, 20, 0, 300, 80));
                features.Add(TestData.Polygon(3, TerrainKind.Clearing, 30, 250, 300, 300));
                break;
            case "waypoints":
                points = [TestData.P(13, 17), TestData.P(13, 17), TestData.P(145, 247), TestData.P(293, 33)];
                break;
            case "coverage-notch":
                return new(CoverageTestData.Repository(),
                    [TestData.P(50, 500), TestData.P(300, 500)], options);
            case "length-limit":
                options = options with { MaxRouteLengthMetres = 30 };
                break;
            case "adaptive-grid":
                MemoryData data = new() { Info = new MemoryData().Info with { Bounds = new(26, 53, 29, 56) } };
                return new(data, [new(54, 27), new(54.001, 27.6)],
                    options with { CellSizeMetres = 200, MaxNodes = 3000 });
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown control scenario.");
        }
        return new(new MemoryData(features.ToArray()), points, options);
    }
}
