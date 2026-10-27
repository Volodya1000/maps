using System.Security.Cryptography;
using System.Text.Json;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using Microsoft.Data.Sqlite;

namespace CableRoutePlanner.Persistence;

public sealed class SqliteGeodataRepository : IGeodataRepository
{
    // A map viewport is a presentation query, not an export query. Keeping this
    // budget bounded is important on Android: Mapsui creates one feature object
    // and one geometry for every row returned here.
    private const int LegacyDisplayFeatureBudget = 6000;
    private readonly string _connectionString;

    private readonly int _schemaVersion;

    public DataPackageInfo Info { get; }

    public SqliteGeodataRepository(string directory)
    {
        string manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new PlanningException("Офлайн-пакет не найден. Укажите каталог с manifest.json.");
        }

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = json.RootElement;
        _schemaVersion = root.GetProperty("schema_version").GetInt32();
        if (_schemaVersion is not (1 or 2) || root.GetProperty("coordinate_system").GetString() != "EPSG:4326")
        {
            throw new PlanningException("Версия или система координат офлайн-пакета не поддерживается.");
        }

        string db = Path.Combine(directory, "geodata.sqlite");
        if (!File.Exists(db))
        {
            throw new PlanningException("Отсутствует база геоданных.");
        }

        using (FileStream stream = File.OpenRead(db))
        {
            string actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(
                root.GetProperty("database_sha256").GetString(),
                StringComparison.OrdinalIgnoreCase))
            {
                throw new PlanningException("Контрольная сумма базы не совпадает. Пакет повреждён.");
            }
        }

        double[] b = root.GetProperty("bounds").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        Info = new(
            root.GetProperty("region_id").GetString()!,
            new(b[0], b[1], b[2], b[3]),
            root.GetProperty("attribution").GetString()!,
            false,
            root.GetProperty("status").GetString()!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = db,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
    }

    public Task<IReadOnlyList<MapFeature>> QueryAsync(GeoBounds bounds, CancellationToken cancellationToken) => QueryInternalAsync(bounds, null, false, cancellationToken);

    public Task<IReadOnlyList<MapFeature>> QueryMapAsync(
        GeoBounds bounds,
        double metresPerPixel,
        CancellationToken cancellationToken)
    {
        // Country overview, regional view and local map have separately prepared geometries.
        // Routing always uses original geometry, so display simplification cannot open an obstacle.
        int? lod = _schemaVersion < 2
            || metresPerPixel <= 10 ? null : metresPerPixel > 250 ? 0 : metresPerPixel > 40 ? 1 : 2;
        return QueryInternalAsync(bounds, lod, true, cancellationToken);
    }

    private Task<IReadOnlyList<MapFeature>> QueryInternalAsync(
        GeoBounds bounds,
        int? lod,
        bool displayRequest,
        CancellationToken cancellationToken) => Task.Run<IReadOnlyList<MapFeature>>(() =>
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        int displayLimit = lod switch
        {
            0 => 500,
            1 => 1_500,
            2 => 5_000,
            _ => LegacyDisplayFeatureBudget
        };
        command.CommandText = lod.HasValue ? """
            SELECT f.id,f.kind,f.wkb,f.name,NULL FROM display_features f
            JOIN display_index i ON i.id=f.id
            WHERE i.minx <= $east AND i.maxx >= $west AND i.miny <= $north AND i.maxy >= $south
              AND f.lod=$lod
            ORDER BY f.id
            LIMIT $display_limit
            """ : displayRequest ? """
            SELECT f.id,f.kind,f.wkb,f.name,f.approval_id FROM features f
            JOIN feature_index i ON i.id=f.id
            WHERE i.minx <= $east AND i.maxx >= $west AND i.miny <= $north AND i.maxy >= $south
            ORDER BY f.id
            LIMIT $display_limit
            """ : """
            SELECT f.id,f.kind,f.wkb,f.name,f.approval_id FROM features f
            JOIN feature_index i ON i.id=f.id
            WHERE i.minx <= $east AND i.maxx >= $west AND i.miny <= $north AND i.maxy >= $south
            ORDER BY f.id
            """;
        if (lod.HasValue)
        {
            command.Parameters.AddWithValue("$lod", lod.Value);
        }

        if (displayRequest)
        {
            command.Parameters.AddWithValue("$display_limit", displayLimit);
        }

        command.Parameters.AddWithValue("$east", bounds.East);
        command.Parameters.AddWithValue("$west", bounds.West);
        command.Parameters.AddWithValue("$north", bounds.North);
        command.Parameters.AddWithValue("$south", bounds.South);
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<MapFeature>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Enum.TryParse<TerrainKind>(reader.GetString(1), out TerrainKind kind))
            {
                throw new PlanningException("Неизвестный тип геообъекта.");
            }

            result.Add(
                new(
                    reader.GetInt64(0),
                    kind,
                    (byte[])reader[2],
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return result;
    }, cancellationToken);
}
