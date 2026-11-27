using System.Globalization;
using System.Text.Json;
using CableRoutePlanner.Domain;

namespace CableRoutePlanner.Persistence;

internal sealed class ElevationTileCatalog
{
    private readonly Tile[] _tiles;
    private readonly double _east;
    private readonly double _north;

    private ElevationTileCatalog(ElevationSource source, Tile[] tiles)
    {
        Source = source;
        _tiles = tiles;
        _east = tiles.Max(tile => tile.East);
        _north = tiles.Max(tile => tile.North);
    }

    public ElevationSource Source { get; }
    public IReadOnlyList<Tile> Tiles => _tiles;

    public Tile? Find(GeoCoordinate point) => Array.Find(_tiles, tile =>
        point.Longitude >= tile.West && (point.Longitude < tile.East || point.Longitude == _east && tile.East == _east)
        && point.Latitude >= tile.South && (point.Latitude < tile.North || point.Latitude == _north && tile.North == _north));

    public void RequireSource(ElevationSource? actual, string directory)
    {
        if (actual != Source)
        {
            throw Invalid("tiles.source", actual?.ToString() ?? directory, Source.ToString());
        }
    }

    public static ElevationTileCatalog Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            JsonElement schema = Property(root, "schema_version", JsonValueKind.Number);
            Require(schema.TryGetInt32(out int version) && version == 1, "schema_version", schema, "version 1");
            JsonElement source = Property(root, "source", JsonValueKind.Object);
            string datum = Text(source, "vertical_datum", "source.");
            Require(datum == "EGM2008", "source.vertical_datum", source.GetProperty("vertical_datum"), "EGM2008");
            JsonElement resolution = Property(source, "resolution_metres", JsonValueKind.Number, "source.");
            Require(resolution.TryGetDouble(out double metres) && double.IsFinite(metres) && metres > 0,
                "source.resolution_metres", resolution, "positive finite metres");
            string license = Text(source, "license_url", "source.");
            if (!Uri.TryCreate(license, UriKind.Absolute, out Uri? uri)
                || uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                throw Invalid("source.license_url", license, "absolute HTTP or HTTPS URI");
            }
            ElevationSource provenance = new(Text(source, "name", "source."), Text(source, "version", "source."),
                datum, metres, Text(source, "attribution", "source."), uri);
            JsonElement tilesJson = Property(root, "tiles", JsonValueKind.Array);
            Require(tilesJson.GetArrayLength() > 0, "tiles", tilesJson, "at least one tile");
            Tile[] tiles = new Tile[tilesJson.GetArrayLength()];
            HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < tiles.Length; index++)
            {
                string prefix = string.Create(CultureInfo.InvariantCulture, $"tiles[{index}].");
                tiles[index] = ReadTile(tilesJson[index], prefix);
                if (!directories.Add(tiles[index].Directory))
                {
                    throw Invalid(prefix + "directory", tiles[index].Directory, "unique tile directory");
                }
                for (int previous = 0; previous < index; previous++)
                {
                    Tile first = tiles[index];
                    Tile second = tiles[previous];
                    if (first.West < second.East && first.East > second.West
                        && first.South < second.North && first.North > second.South)
                    {
                        throw Invalid(prefix + "bounds", tilesJson[index].GetRawText(), "non-overlapping ownership bounds");
                    }
                }
            }
            return new(provenance, tiles);
        }
        catch (JsonException error)
        {
            throw Invalid("index.json", error.BytePositionInLine?.ToString(CultureInfo.InvariantCulture) ?? "invalid JSON",
                "valid JSON", error);
        }
    }

    private static Tile ReadTile(JsonElement json, string prefix)
    {
        string directory = Text(json, "directory", prefix);
        string[] segments = directory.Split('/');
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment.EndsWith('.')
            || segment.Any(character => character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z'
                or >= '0' and <= '9' or '-' or '_' or '.'))))
        {
            throw Invalid(prefix + "directory", directory, "safe relative directory segments separated by /");
        }
        JsonElement bounds = Property(json, "bounds", JsonValueKind.Array, prefix);
        Require(bounds.GetArrayLength() == 4, prefix + "bounds", bounds, "[west, south, east, north]");
        double[] values = new double[4];
        for (int index = 0; index < values.Length; index++)
        {
            Require(bounds[index].ValueKind == JsonValueKind.Number && bounds[index].TryGetDouble(out values[index])
                && double.IsFinite(values[index]), prefix + "bounds", bounds, "finite coordinates");
        }
        Require(values[0] >= -180 && values[2] <= 180 && values[1] >= -90 && values[3] <= 90
            && values[0] < values[2] && values[1] < values[3], prefix + "bounds", bounds, "ordered EPSG:4326 bounds");
        return new(directory, values[0], values[1], values[2], values[3]);
    }

    private static JsonElement Property(JsonElement obj, string name, JsonValueKind kind, string prefix = "")
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out JsonElement value))
        {
            throw Invalid(prefix + name, "<missing>", kind.ToString());
        }
        Require(value.ValueKind == kind, prefix + name, value, kind.ToString());
        return value;
    }

    private static string Text(JsonElement obj, string name, string prefix)
    {
        JsonElement value = Property(obj, name, JsonValueKind.String, prefix);
        return !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!
            : throw Invalid(prefix + name, value.GetRawText(), "nonempty text");
    }

    private static void Require(bool condition, string field, JsonElement value, string rule)
    {
        if (!condition)
        {
            throw Invalid(field, value.GetRawText(), rule);
        }
    }

    internal static InvalidDataException Invalid(string field, string value, string rule, Exception? inner = null)
    {
        InvalidDataException error = new($"Elevation field {field} has value {value}; expected {rule}.", inner);
        error.Data["Field"] = field;
        error.Data["Value"] = value;
        error.Data["Rule"] = rule;
        return error;
    }

    internal sealed record Tile(string Directory, double West, double South, double East, double North);
}
