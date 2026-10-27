using System.Security.Cryptography;
using System.Text.Json;
using CableRoutePlanner.Application;
using CableRoutePlanner.Persistence;
using Microsoft.Data.Sqlite;
using NetTopologySuite.IO;

namespace CableRoutePlanner.Tests;

internal sealed class SqliteGeodataFixture : IDisposable
{
    private static readonly double[] SyntheticBounds = [27.0, 54.0, 27.006, 54.006];
    private readonly TemporaryDirectory _directory = new();
    public string DirectoryPath => _directory.DirectoryPath;
    public SqliteGeodataRepository Repository { get; }

    public SqliteGeodataFixture(params MapFeature[] features)
    {
        try
        {
            string database = Path.Combine(DirectoryPath, "geodata.sqlite");
            CreateDatabase(database, features);
            using FileStream input = File.OpenRead(database);
            string checksum = Convert.ToHexString(SHA256.HashData(input));
            File.WriteAllText(Path.Combine(DirectoryPath, "manifest.json"), JsonSerializer.Serialize(new
            {
                schema_version = 2,
                coordinate_system = "EPSG:4326",
                database_sha256 = checksum,
                bounds = SyntheticBounds,
                region_id = "synthetic-test-only",
                attribution = "test",
                status = "synthetic fixture"
            }));
            Repository = new SqliteGeodataRepository(DirectoryPath);
        }
        catch
        {
            _directory.Dispose();
            throw;
        }
    }

    private static void CreateDatabase(string path, IEnumerable<MapFeature> features)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE features(id INTEGER PRIMARY KEY, kind TEXT, wkb BLOB, name TEXT, approval_id TEXT);
            CREATE VIRTUAL TABLE feature_index USING rtree(id,minx,maxx,miny,maxy);
            CREATE TABLE display_features(id INTEGER PRIMARY KEY, kind TEXT, wkb BLOB, name TEXT, lod INTEGER);
            CREATE VIRTUAL TABLE display_index USING rtree(id,minx,maxx,miny,maxy);
            """;
        command.ExecuteNonQuery();
        foreach (MapFeature feature in features)
        {
            NetTopologySuite.Geometries.Envelope envelope = new WKBReader().Read(feature.Wkb).EnvelopeInternal;
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO features VALUES($id,$kind,$wkb,$name,$approval);
                INSERT INTO feature_index VALUES($id,$west,$east,$south,$north);
                """;
            command.Parameters.AddWithValue("$id", feature.Id);
            command.Parameters.AddWithValue("$kind", feature.Kind.ToString());
            command.Parameters.AddWithValue("$wkb", feature.Wkb);
            command.Parameters.AddWithValue("$name", feature.Name);
            command.Parameters.AddWithValue("$approval", (object?)feature.ApprovalId ?? DBNull.Value);
            command.Parameters.AddWithValue("$west", envelope.MinX);
            command.Parameters.AddWithValue("$east", envelope.MaxX);
            command.Parameters.AddWithValue("$south", envelope.MinY);
            command.Parameters.AddWithValue("$north", envelope.MaxY);
            command.ExecuteNonQuery();
        }
    }

    public void Dispose() => _directory.Dispose();
}
