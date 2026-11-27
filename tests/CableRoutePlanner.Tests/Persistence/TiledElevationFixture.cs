using System.Buffers.Binary;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CableRoutePlanner.Tests;

internal static class TiledElevationFixture
{
    private static readonly double[] WestBounds = [27.0, 53.0, 27.25, 53.25];
    private static readonly double[] EastBounds = [27.25, 53.0, 27.5, 53.25];
    public static string Catalog(string first = "west", string second = "east", string version = "fixture") =>
        JsonSerializer.Serialize(new
        {
            schema_version = 1,
            source = new
            {
                name = "Test DEM",
                version,
                vertical_datum = "EGM2008",
                resolution_metres = 30,
                attribution = "Test attribution",
                license_url = "https://example.test/license"
            },
            tiles = new[] { new { directory = first, bounds = WestBounds },
                new { directory = second, bounds = EastBounds } }
        });

    public static void Create(string root, string version = "fixture", int heightOffset = 0, bool compressed = false, string? invalidBlock = null)
    {
        string elevation = Path.Combine(root, "elevation");
        Directory.CreateDirectory(elevation);
        File.WriteAllText(Path.Combine(elevation, "index.json"), Catalog(version: version));
        CreateTile(Path.Combine(elevation, "west"), 27, version, heightOffset, compressed, invalidBlock);
        CreateTile(Path.Combine(elevation, "east"), 27.25, version, heightOffset, compressed, invalidBlock);
    }

    private static void CreateTile(string directory, double west, string version, int heightOffset, bool compressed, string? invalidBlock)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "elevation.sqlite");
        using (SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE metadata (id INTEGER PRIMARY KEY, json TEXT NOT NULL);
                CREATE TABLE elevation_blocks (grid_id INTEGER, block_row INTEGER, block_column INTEGER, samples BLOB);
                INSERT INTO metadata VALUES (1, $metadata);
                INSERT INTO elevation_blocks VALUES (1, 0, 0, $samples);
                """;
            command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(new
            {
                schema_version = compressed ? 2 : 1,
                block_encoding = compressed ? "zlib-float32-le" : null,
                coordinate_system = "EPSG:4326",
                block_size = 128,
                source_name = "Test DEM",
                source_version = version,
                vertical_datum = "EGM2008",
                resolution_metres = 30,
                attribution = "Test attribution",
                license_url = "https://example.test/license",
                // One native pixel of padding on every ownership edge.
                grids = new[] { new { id = 1, width = 6, height = 6,
                    affine = new[] { 0.0625, 0, west - 0.0625, 0, -0.0625, 53.3125 },
                    input_file = "fixture.tif", input_sha256 = new string('a', 64) } }
            }));
            byte[] samples = new byte[128 * 128 * sizeof(float)];
            for (int row = 0; row < 128; row++)
            {
                for (int column = 0; column < 128; column++)
                {
                    float value = (float)((west - 27) * 16 + column - 0.5 + row * 2 + heightOffset);
                    BinaryPrimitives.WriteSingleLittleEndian(samples.AsSpan((row * 128 + column) * sizeof(float)), value);
                }
            }
            if (compressed)
            {
                int size = invalidBlock switch { "short" => samples.Length - 1, "oversized" => samples.Length + 1, _ => samples.Length };
                using MemoryStream output = new();
                using (ZLibStream zlib = new(output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    zlib.Write(size > samples.Length ? new byte[size] : samples.AsSpan(0, size));
                }
                samples = output.ToArray();
                samples = invalidBlock switch
                {
                    "invalid" => [1, 2, 3],
                    "truncated" => samples[..^1],
                    _ => samples
                };
            }
            command.Parameters.AddWithValue("$samples", samples);
            command.ExecuteNonQuery();
        }
        File.WriteAllText(path + ".sha256", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }
}
