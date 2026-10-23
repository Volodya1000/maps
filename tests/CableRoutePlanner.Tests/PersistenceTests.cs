using System.Security.Cryptography;
using System.Text.Json;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Routing;
using CableRoutePlanner.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task SimplifiedDisplayCannotRemoveAnObstacleFromRouting()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, "geodata.sqlite");
            using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE features(id INTEGER PRIMARY KEY, kind TEXT, wkb BLOB, name TEXT, approval_id TEXT);
                    CREATE VIRTUAL TABLE feature_index USING rtree(id,minx,maxx,miny,maxy);
                    CREATE TABLE display_features(id INTEGER PRIMARY KEY, kind TEXT, wkb BLOB, name TEXT, lod INTEGER);
                    CREATE VIRTUAL TABLE display_index USING rtree(id,minx,maxx,miny,maxy);
                    """;
                command.ExecuteNonQuery();
                var marsh = TestData.Polygon(1, TerrainKind.Marsh, 151, -20, 153, 700);
                var shape = new NetTopologySuite.IO.WKBReader().Read(marsh.Wkb);
                var envelope = shape.EnvelopeInternal;
                command.CommandText = """
                    INSERT INTO features VALUES(1,'Marsh',$wkb,'synthetic fixture',NULL);
                    INSERT INTO feature_index VALUES(1,$west,$east,$south,$north);
                    """;
                command.Parameters.AddWithValue("$wkb", marsh.Wkb);
                command.Parameters.AddWithValue("$west", envelope.MinX);
                command.Parameters.AddWithValue("$east", envelope.MaxX);
                command.Parameters.AddWithValue("$south", envelope.MinY);
                command.Parameters.AddWithValue("$north", envelope.MaxY);
                command.ExecuteNonQuery();
            }

            File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
            {
                schema_version = 2,
                coordinate_system = "EPSG:4326",
                database_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(database))),
                bounds = new[] { 27.0, 54.0, 27.006, 54.006 },
                region_id = "synthetic-test-only",
                attribution = "test",
                status = "synthetic fixture"
            }));
            var repository = new SqliteGeodataRepository(directory);
            Assert.Empty(await repository.QueryMapAsync(repository.Info.Bounds, 1500, default));
            Assert.Equal(TerrainKind.Marsh, Assert.Single(await repository.QueryAsync(repository.Info.Bounds, default)).Kind);
            var planner = new GridRoutePlanner(repository, NullLogger<GridRoutePlanner>.Instance);
            await Assert.ThrowsAsync<CableRoutePlanner.Application.PlanningException>(() => planner.PlanAsync(
                new[] { TestData.P(50, 150), TestData.P(260, 150) }, TestData.Options, default));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
