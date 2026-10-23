using System.Security.Cryptography;
using System.Text.Json;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using Microsoft.Data.Sqlite;
namespace CableRoutePlanner.Infrastructure.Persistence;

public sealed class SqliteGeodataRepository : IGeodataRepository
{
    private readonly string _connectionString;
    public DataPackageInfo Info { get; }
    public SqliteGeodataRepository(string directory)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath)) throw new PlanningException("Офлайн-пакет не найден. Укажите каталог с manifest.json.");
        using var json = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = json.RootElement;
        if (root.GetProperty("schema_version").GetInt32() != 1 || root.GetProperty("coordinate_system").GetString() != "EPSG:4326")
            throw new PlanningException("Версия или система координат офлайн-пакета не поддерживается.");
        var db = Path.Combine(directory, "geodata.sqlite");
        if (!File.Exists(db)) throw new PlanningException("Отсутствует база геоданных.");
        using (var stream = File.OpenRead(db))
        {
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(root.GetProperty("database_sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new PlanningException("Контрольная сумма базы не совпадает. Пакет повреждён.");
        }
        var b = root.GetProperty("bounds").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        Info = new(root.GetProperty("region_id").GetString()!, new(b[0], b[1], b[2], b[3]),
            root.GetProperty("attribution").GetString()!, false,
            root.GetProperty("status").GetString()!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
    }
    public Task<IReadOnlyList<MapFeature>> QueryAsync(GeoBounds bounds, CancellationToken cancellationToken) => Task.Run<IReadOnlyList<MapFeature>>(() =>
    {
        using var connection = new SqliteConnection(_connectionString); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.id,f.kind,f.wkb,f.name,f.approval_id FROM features f
            JOIN feature_index i ON i.id=f.id
            WHERE i.minx <= $east AND i.maxx >= $west AND i.miny <= $north AND i.maxy >= $south
            ORDER BY f.id
            """;
        command.Parameters.AddWithValue("$east", bounds.East); command.Parameters.AddWithValue("$west", bounds.West);
        command.Parameters.AddWithValue("$north", bounds.North); command.Parameters.AddWithValue("$south", bounds.South);
        using var reader = command.ExecuteReader();
        var result = new List<MapFeature>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Enum.TryParse<TerrainKind>(reader.GetString(1), out var kind)) throw new PlanningException("Неизвестный тип геообъекта.");
            if (result.Count >= 50000) throw new PlanningException("Слишком много объектов. Приблизьте карту или уменьшите область расчёта.");
            result.Add(new(reader.GetInt64(0), kind, (byte[])reader[2], reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return result;
    }, cancellationToken);
}
