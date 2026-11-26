using CableRoutePlanner.Domain;
using CableRoutePlanner.Persistence;
using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class CompressedElevationProviderTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    public void CompressedSchemaRequiresItsExactEncodingBeforeReadingAnyBlocks(string scenario)
    {
        using TemporaryDirectory directory = new();
        TiledElevationFixture.Create(directory.DirectoryPath, compressed: true);
        string tile = Path.Combine(directory.DirectoryPath, "elevation", "west");
        using (SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(tile, "elevation.sqlite"),
            Pooling = false
        }.ToString()))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT json FROM metadata WHERE id = 1";
            JsonObject metadata = JsonNode.Parse(Assert.IsType<string>(command.ExecuteScalar()))!.AsObject();
            if (scenario == "missing")
            {
                metadata.Remove("block_encoding");
            }
            else
            {
                metadata["block_encoding"] = "gzip";
            }
            command.CommandText = "UPDATE metadata SET json = $metadata WHERE id = 1";
            command.Parameters.AddWithValue("$metadata", metadata.ToJsonString());
            command.ExecuteNonQuery();
        }
        SqliteElevationProvider provider = new(tile);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => provider.Source);

        Assert.Equal("block_encoding", error.Data["Field"]);
        Assert.NotNull(error.Data["Value"]);
        Assert.NotNull(error.Data["Rule"]);
    }

    [Fact]
    public async Task CompressedCountryShardsProduceIdenticalNativeSeamHeightsToLegacyRawBlocks()
    {
        using TemporaryDirectory raw = new();
        using TemporaryDirectory compressed = new();
        TiledElevationFixture.Create(raw.DirectoryPath);
        TiledElevationFixture.Create(compressed.DirectoryPath, compressed: true);
        GeoCoordinate[] positions = [new(53.125, 27.249), new(53.125, 27.25), new(53.125, 27.251), new(53.25, 27.0)];
        IReadOnlyList<double?> expected = await new TiledElevationProvider(raw.DirectoryPath).ReadAsync(positions, default);

        TiledElevationProvider provider = new(compressed.DirectoryPath);
        IReadOnlyList<double?> actual = await provider.ReadAsync(positions, default);

        Assert.Equal(expected, actual);
        Assert.Equal("fixture", provider.Source?.Version);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("short")]
    [InlineData("oversized")]
    [InlineData("truncated")]
    public async Task InvalidCompressedBlocksFailWithStructuredDataErrorAndCleanSnapshots(string scenario)
    {
        using TemporaryDirectory directory = new();
        using TemporaryDirectory snapshots = new();
        TiledElevationFixture.Create(directory.DirectoryPath, compressed: true, invalidBlock: scenario);
        TiledElevationProvider provider = new(directory.DirectoryPath, snapshots.DirectoryPath);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.ReadAsync([new(53.125, 27.125)], default));

        Assert.Equal("elevation_blocks.samples", error.Data["Field"]);
        Assert.NotNull(error.Data["Value"]);
        Assert.NotNull(error.Data["Rule"]);
        Assert.Empty(Directory.EnumerateFileSystemEntries(snapshots.DirectoryPath));
    }
}
