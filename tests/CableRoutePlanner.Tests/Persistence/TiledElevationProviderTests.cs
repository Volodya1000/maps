using System.Text.Json.Nodes;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class TiledElevationProviderTests
{
    [Fact]
    public async Task OwnershipIncludesOuterEdgesButNeverExposesPaddingOutsideTheCatalog()
    {
        using TemporaryDirectory directory = new();
        TiledElevationFixture.Create(directory.DirectoryPath);
        TiledElevationProvider provider = new(directory.DirectoryPath);
        const double delta = 1.0 / 1024;

        IReadOnlyList<double?> heights = await provider.ReadAsync(
            [new(53.25, 27.125), new(53, 27.125), new(53.25, 27.5), new(53, 27.5),
                new(53.125, 27 - delta), new(53.125, 27.5 + delta),
                new(53 - delta, 27.125), new(53.25 + delta, 27.125)], default);

        Assert.Equal(new double?[] { 3, 11, 9, 17, null, null, null, null }, heights);
    }

    [Fact]
    public async Task SharedMeridianBelongsToTheEasternTileAndEmptyRequestsOpenNoShards()
    {
        using TemporaryDirectory directory = new();
        TiledElevationFixture.Create(directory.DirectoryPath);
        File.Delete(Path.Combine(directory.DirectoryPath, "elevation", "west", "elevation.sqlite"));
        TiledElevationProvider provider = new(directory.DirectoryPath);

        Assert.Empty(await provider.ReadAsync([], default));
        Assert.Equal(9, Assert.Single(await provider.ReadAsync([new(53.125, 27.25)], default)));
    }

    [Fact]
    public async Task CatalogShardChecksumCannotBecomeOptionalAfterTheOwnershipCheck()
    {
        using TemporaryDirectory directory = new();
        TiledElevationFixture.Create(directory.DirectoryPath);
        string tileDirectory = Path.Combine(directory.DirectoryPath, "elevation", "west");
        SqliteElevationProvider strict = new(tileDirectory, File.OpenRead, requireChecksum: true);
        Assert.Equal("fixture", strict.Source?.Version);
        File.Delete(Path.Combine(tileDirectory, "elevation.sqlite.sha256"));

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            strict.ReadAsync([new(53.125, 27.125)], default));

        Assert.Equal("elevation.sqlite.sha256", error.Data["Field"]);
        Assert.Equal(7, Assert.Single(await new SqliteElevationProvider(tileDirectory)
            .ReadAsync([new(53.125, 27.125)], default)));
    }

    [Fact]
    public async Task CountryCatalogTakesPrecedenceOverLegacyDemAndPreservesPointOrderAcrossPaddedSeams()
    {
        using TemporaryDirectory directory = new();
        using TemporaryDirectory snapshots = new();
        TiledElevationFixture.Create(directory.DirectoryPath);
        File.WriteAllText(Path.Combine(directory.DirectoryPath, "elevation.sqlite"), "obsolete tiny DEM");
        using ServiceProvider services = new ServiceCollection()
            .AddPersistence(directory.DirectoryPath, Path.Combine(directory.DirectoryPath, "settings.json"), snapshots.DirectoryPath)
            .BuildServiceProvider();
        IElevationProvider provider = services.GetRequiredService<IElevationProvider>();
        const double delta = 1.0 / 1024;

        IReadOnlyList<double?> heights = await provider.ReadAsync(
            [new(53.125, 27.25 + delta), new(53.125, 28), new(53.125, 27.25 - delta),
                new(53.125, 27.25), new(53.125, 27.0), new(53.125, 27.5)], default);

        Assert.IsType<TiledElevationProvider>(provider);
        Assert.Equal("fixture", provider.Source?.Version);
        Assert.Equal(new double?[] { 9 + 16 * delta, null, 9 - 16 * delta, 9, 5, 13 }, heights);
        Assert.Empty(Directory.EnumerateFileSystemEntries(snapshots.DirectoryPath));
    }

    [Fact]
    public async Task CatalogSourceAndUnrelatedShardAreNotDependentOnOpeningTheWholeCountry()
    {
        using TemporaryDirectory directory = new();
        TiledElevationFixture.Create(directory.DirectoryPath);
        File.Delete(Path.Combine(directory.DirectoryPath, "elevation", "east", "elevation.sqlite"));
        TiledElevationProvider provider = new(directory.DirectoryPath);

        Assert.Equal("Test DEM", provider.Source?.Name);
        Assert.Equal(7, Assert.Single(await provider.ReadAsync([new(53.125, 27.125)], default)));
        Assert.Null(Assert.Single(await provider.ReadAsync([new(54, 28)], default)));
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.ReadAsync([new(53.125, 27.375)], default));

        Assert.Equal("tiles.directory", error.Data["Field"]);
        Assert.Equal("east", error.Data["Value"]);
        Assert.NotNull(error.Data["Rule"]);
    }

    [Fact]
    public async Task MissingCatalogReturnsMissingDemAndPreCancellationDoesNotParseInvalidJson()
    {
        using TemporaryDirectory directory = new();
        TiledElevationProvider absent = new(directory.DirectoryPath);
        Assert.Null(absent.Source);
        Assert.Null(Assert.Single(await absent.ReadAsync([new(53, 27)], default)));
        Directory.CreateDirectory(Path.Combine(directory.DirectoryPath, "elevation"));
        File.WriteAllText(Path.Combine(directory.DirectoryPath, "elevation", "index.json"), "invalid JSON");
        TiledElevationProvider invalid = new(directory.DirectoryPath);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            invalid.ReadAsync([new(53, 27)], new CancellationToken(canceled: true)));
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => invalid.Source);

        Assert.Equal("index.json", error.Data["Field"]);
    }

    [Fact]
    public async Task DifferentShardProvenanceIsRejectedBeforeReturningPlausibleHeights()
    {
        using TemporaryDirectory directory = new();
        TiledElevationFixture.Create(directory.DirectoryPath);
        File.WriteAllText(Path.Combine(directory.DirectoryPath, "elevation", "index.json"),
            TiledElevationFixture.Catalog(version: "different"));
        TiledElevationProvider provider = new(directory.DirectoryPath);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.ReadAsync([new(53.125, 27.125)], default));

        Assert.Equal("tiles.source", error.Data["Field"]);
    }

    [Fact]
    public async Task EachReadVerifiesClaimedShardBytesAndRequiresItsChecksum()
    {
        using TemporaryDirectory directory = new();
        TiledElevationFixture.Create(directory.DirectoryPath);
        TiledElevationProvider provider = new(directory.DirectoryPath);
        Assert.Equal(7, Assert.Single(await provider.ReadAsync([new(53.125, 27.125)], default)));
        string database = Path.Combine(directory.DirectoryPath, "elevation", "west", "elevation.sqlite");
        byte[] bytes = File.ReadAllBytes(database);
        bytes[^1] ^= 1;
        File.WriteAllBytes(database, bytes);

        InvalidDataException corrupt = await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.ReadAsync([new(53.125, 27.125)], default));
        Assert.Equal("elevation.sqlite.sha256", corrupt.Data["Field"]);
        File.Delete(database + ".sha256");
        InvalidDataException missing = await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.ReadAsync([new(53.125, 27.125)], default));
        Assert.Equal("tiles.directory", missing.Data["Field"]);
    }

    [Theory]
    [InlineData("traversal", "tiles[0].directory")]
    [InlineData("rooted", "tiles[0].directory")]
    [InlineData("windows", "tiles[0].directory")]
    [InlineData("alias", "tiles[0].directory")]
    [InlineData("duplicate", "tiles[1].directory")]
    [InlineData("overlap", "tiles[1].bounds")]
    [InlineData("reversed", "tiles[0].bounds")]
    [InlineData("overflow", "tiles[0].bounds")]
    [InlineData("nonfinite", "tiles[0].bounds")]
    [InlineData("missing", "source.attribution")]
    [InlineData("datum", "source.vertical_datum")]
    [InlineData("resolution", "source.resolution_metres")]
    [InlineData("license", "source.license_url")]
    [InlineData("empty", "tiles")]
    [InlineData("schema", "schema_version")]
    public void InvalidCatalogIdentifiesFieldValueAndRule(string scenario, string field)
    {
        using TemporaryDirectory directory = new();
        string elevation = Path.Combine(directory.DirectoryPath, "elevation");
        Directory.CreateDirectory(elevation);
        JsonObject json = JsonNode.Parse(TiledElevationFixture.Catalog())!.AsObject();
        switch (scenario)
        {
            case "traversal":
                json["tiles"]![0]!["directory"] = "../escape";
                break;
            case "rooted":
                json["tiles"]![0]!["directory"] = "/escape";
                break;
            case "windows":
                json["tiles"]![0]!["directory"] = "C:\\escape";
                break;
            case "alias":
                json["tiles"]![0]!["directory"] = "west.";
                break;
            case "duplicate":
                json["tiles"]![1]!["directory"] = "WEST";
                break;
            case "overlap":
                json["tiles"]![1]!["bounds"]![0] = 27.125;
                break;
            case "reversed":
                json["tiles"]![0]!["bounds"]![2] = 26;
                break;
            case "overflow":
                json["tiles"]![0]!["bounds"]![2] = 181;
                break;
            case "missing":
                json["source"]!.AsObject().Remove("attribution");
                break;
            case "datum":
                json["source"]!["vertical_datum"] = "unknown";
                break;
            case "resolution":
                json["source"]!["resolution_metres"] = 0;
                break;
            case "license":
                json["source"]!["license_url"] = "file:///license";
                break;
            case "empty":
                json["tiles"] = new JsonArray();
                break;
            case "schema":
                json["schema_version"] = 2;
                break;
        }
        string serialized = json.ToJsonString();
        if (scenario == "nonfinite")
        {
            serialized = serialized.Replace("27.25", "1e999", StringComparison.Ordinal);
        }
        File.WriteAllText(Path.Combine(elevation, "index.json"), serialized);
        TiledElevationProvider provider = new(directory.DirectoryPath);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => provider.Source);

        Assert.Equal(field, error.Data["Field"]);
        Assert.NotNull(error.Data["Value"]);
        Assert.NotNull(error.Data["Rule"]);
    }
}
