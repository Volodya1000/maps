using System.IO.Compression;
using System.Text.Json;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Persistence;
using NetTopologySuite.Geometries;

namespace CableRoutePlanner.Tests;

internal sealed class SourceRoutingCaseFixture : IDisposable
{
    private readonly SqliteGeodataFixture _database;

    public GeoCoordinate[] Points { get; }

    public RoutingOptions Options { get; }

    public MapFeature[] Features { get; }

    public GeoCoordinate[] Witness { get; }

    public GeoCoverage Coverage { get; }

    public GeoBounds SourceQueryBounds { get; }

    public SqliteGeodataRepository Repository { get; }

    public SourceRoutingCaseFixture(string caseId)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SourceRouting", caseId + ".json.gz");
        using FileStream input = File.OpenRead(path);
        using GZipStream compressed = new(input, CompressionMode.Decompress);
        using JsonDocument document = JsonDocument.Parse(compressed);
        JsonElement root = document.RootElement;
        JsonElement scenario = root.GetProperty("originalScenario");
        JsonElement options = scenario.GetProperty("routingOptions");
        Points = scenario.GetProperty("points").EnumerateArray().Select(ReadCoordinate).ToArray();
        Options = new(
            options.GetProperty("CellSizeMetres").GetDouble(),
            options.GetProperty("MaxNodes").GetInt32(),
            options.GetProperty("ReserveFraction").GetDouble(),
            options.GetProperty("PreferOverheadGasCrossings").GetBoolean(),
            options.GetProperty("MaxRouteLengthMetres").GetDouble(),
            options.TryGetProperty("AllowExperimentalPedestrianUnderpasses", out JsonElement underpasses)
                && underpasses.GetBoolean());
        Features = root.GetProperty("features").EnumerateArray().Select(ReadFeature).ToArray();
        JsonElement package = root.GetProperty("packageInfo");
        GeoCoverage originalCoverage = new(package.GetProperty("coverage").EnumerateArray()
            .Select(point => new GeoCoordinate(point[1].GetDouble(), point[0].GetDouble())));
        SourceQueryBounds = ReadBounds(root.GetProperty("sourceQueryBounds"));
        GeoCoordinate[] boundary =
        [
            new(SourceQueryBounds.South, SourceQueryBounds.West),
            new(SourceQueryBounds.South, SourceQueryBounds.East),
            new(SourceQueryBounds.North, SourceQueryBounds.East),
            new(SourceQueryBounds.North, SourceQueryBounds.West),
            new(SourceQueryBounds.South, SourceQueryBounds.West)
        ];
        GeometryFactory factory = new();
        Polygon country = factory.CreatePolygon(originalCoverage.Boundary
            .Select(point => new Coordinate(point.Longitude, point.Latitude)).ToArray());
        Polygon window = factory.CreatePolygon(boundary
            .Select(point => new Coordinate(point.Longitude, point.Latitude)).ToArray());
        if (!country.Covers(window))
        {
            throw new InvalidDataException("The source query rectangle must be completely covered by the original package boundary.");
        }
        Coverage = new(boundary);
        Witness = root.GetProperty("independentWitness").GetProperty("alternative").GetProperty("geometry")
            .EnumerateArray().Select(ReadCoordinate).ToArray();
        // The JSON retains the original country manifest as provenance. This
        // isolated subset advertises only the rectangle whose data it includes.
        _database = new(Features, null, Coverage, "source-routing-subset", SourceQueryBounds,
            package.GetProperty("manifest").GetProperty("schema_version").GetInt32());
        Repository = _database.Repository;
    }

    private static GeoCoordinate ReadCoordinate(JsonElement point) =>
        new(point.GetProperty("Latitude").GetDouble(), point.GetProperty("Longitude").GetDouble());

    private static GeoBounds ReadBounds(JsonElement bounds) =>
        new(bounds[0].GetDouble(), bounds[1].GetDouble(), bounds[2].GetDouble(), bounds[3].GetDouble());

    private static MapFeature ReadFeature(JsonElement feature)
    {
        UnderpassTopology? topology = null;
        if (feature.TryGetProperty("underpassTopology", out JsonElement network)
            && network.ValueKind != JsonValueKind.Null)
        {
            topology = new(network.GetProperty("networkId").GetInt64(),
                network.GetProperty("nodeIds").EnumerateArray().Select(node => node.GetInt64()).ToArray(),
                network.GetProperty("entranceNodeIds").EnumerateArray().Select(node => node.GetInt64()).ToArray());
        }
        long[]? roadIds = feature.TryGetProperty("underpassRoadIds", out JsonElement roads)
            && roads.ValueKind != JsonValueKind.Null
                ? roads.EnumerateArray().Select(road => road.GetInt64()).ToArray() : null;
        return new(feature.GetProperty("id").GetInt64(),
            Enum.Parse<TerrainKind>(feature.GetProperty("kind").GetString()!),
            Convert.FromBase64String(feature.GetProperty("wkbBase64").GetString()!),
            feature.GetProperty("name").GetString()!, feature.GetProperty("approvalId").GetString(),
            roadIds, topology);
    }

    public void Dispose() => _database.Dispose();
}
