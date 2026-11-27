using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class SqliteElevationProviderTests
{
    private const int BlockSize = 128;
    private const double LongitudeStep = 1.0 / 4096;
    private const double LatitudeStep = 1.0 / 2048;

    [Fact]
    public async Task OptionalPackageExposesNoSourceOrElevations()
    {
        using TemporaryDirectory directory = new();
        using ServiceProvider services = CreateServices(directory.DirectoryPath);
        IElevationProvider provider = services.GetRequiredService<IElevationProvider>();

        IReadOnlyList<double?> heights = await provider.ReadAsync([new(54, 27), new(54, 28)], default);

        Assert.Null(provider.Source);
        Assert.Equal(new double?[] { null, null }, heights);
        Assert.False(File.Exists(Path.Combine(directory.DirectoryPath, "elevation.sqlite")));
    }

    [Fact]
    public async Task ConfiguredTemporaryDirectoryIsUsedAndCleared()
    {
        using TemporaryDirectory directory = new();
        CreatePackage(directory.DirectoryPath);
        string snapshotRoot = Path.Combine(directory.DirectoryPath, "snapshots");
        File.WriteAllText(snapshotRoot, "This file occupies the configured directory path.");
        using ServiceProvider services = CreateServices(directory.DirectoryPath, snapshotRoot);
        IElevationProvider provider = services.GetRequiredService<IElevationProvider>();
        GeoCoordinate[] positions = [AtPixelEdge(128, 1)];

        await Assert.ThrowsAnyAsync<IOException>(() => provider.ReadAsync(positions, default));
        File.Delete(snapshotRoot);
        Directory.CreateDirectory(snapshotRoot);

        Assert.Equal(270, Assert.Single(await provider.ReadAsync(positions, default)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(snapshotRoot));
    }

    [Fact]
    public async Task InterpolationReadsAcrossBlockAndGridSeamsOnTheNativeAffine()
    {
        using TemporaryDirectory directory = new();
        CreatePackage(directory.DirectoryPath);
        using ServiceProvider services = CreateServices(directory.DirectoryPath);
        IElevationProvider provider = services.GetRequiredService<IElevationProvider>();
        ElevationSource source = Assert.IsType<ElevationSource>(provider.Source);

        IReadOnlyList<double?> heights = await provider.ReadAsync(
            [AtPixelEdge(128, 1), AtPixelEdge(130, 1), AtPixelEdge(128.5, 0.5), AtPixelEdge(128.25, 0.75), AtPixelEdge(0.25, 1), new(54, 28)],
            default);

        Assert.Equal("Test DEM", source.Name);
        Assert.Equal("fixture-release", source.Version);
        Assert.Equal("EGM2008", source.VerticalDatum);
        Assert.Equal(30, source.ResolutionMetres);
        Assert.Equal("Test attribution", source.Attribution);
        Assert.Equal(new Uri("https://example.test/license"), source.LicenseUri);
        Assert.Equal(new double?[] { 270, 274, 266, 268, null, null }, heights);
    }

    [Fact]
    public async Task LatitudeBandSeamUsesEachNativeRowAtTheRequestedLongitude()
    {
        using TemporaryDirectory directory = new();
        CreatePackage(directory.DirectoryPath, latitudeBandSeam: true);
        using ServiceProvider services = CreateServices(directory.DirectoryPath);
        IElevationProvider provider = services.GetRequiredService<IElevationProvider>();

        IReadOnlyList<double?> heights = await provider.ReadAsync(
            [AtPixelEdge(1.5, 2 - 1.0 / 1024), AtPixelEdge(1.5, 2), AtPixelEdge(1.5, 2 + 1.0 / 1024)], default);

        Assert.Equal(new double?[] { 45 - 90.0 / 1024, 45, 45 + 90.0 / 1024 }, heights);
    }

    [Fact]
    public async Task MissingContributingNeighbourDoesNotBecomeZeroOrFillAnInterpolation()
    {
        using TemporaryDirectory directory = new();
        CreatePackage(directory.DirectoryPath, noData: true);
        using ServiceProvider services = CreateServices(directory.DirectoryPath);
        IElevationProvider provider = services.GetRequiredService<IElevationProvider>();

        IReadOnlyList<double?> heights = await provider.ReadAsync(
            [AtPixelEdge(128, 1), AtPixelEdge(128.5, 0.5), AtPixelEdge(128.5, 1.5)], default);

        Assert.Equal(new double?[] { null, 266, null }, heights);
    }

    [Theory]
    [InlineData("EPSG:3857", 0, "coordinate_system")]
    [InlineData("EPSG:4326", 0.25, "grids[0].affine")]
    public void InvalidMetadataIdentifiesTheFailingField(string coordinateSystem, double rotation, string field)
    {
        using TemporaryDirectory directory = new();
        CreatePackage(directory.DirectoryPath, coordinateSystem: coordinateSystem, rotation: rotation);
        using ServiceProvider services = CreateServices(directory.DirectoryPath);
        IElevationProvider provider = services.GetRequiredService<IElevationProvider>();

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => provider.Source);

        Assert.Equal(field, error.Data["Field"]);
    }

    [Fact]
    public async Task MissingBlockInAPresentPackageRaisesADataError()
    {
        using TemporaryDirectory directory = new();
        CreatePackage(directory.DirectoryPath, omitBlock: true);
        using ServiceProvider services = CreateServices(directory.DirectoryPath);
        IElevationProvider provider = services.GetRequiredService<IElevationProvider>();

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.ReadAsync([AtPixelEdge(128, 1)], default));

        Assert.Equal("elevation_blocks.samples", error.Data["Field"]);
    }

    [Fact]
    public async Task CancelledReadDoesNotOpenAnInvalidPackage()
    {
        using TemporaryDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.DirectoryPath, "elevation.sqlite"), "invalid sqlite");
        using ServiceProvider services = CreateServices(directory.DirectoryPath);
        IElevationProvider provider = services.GetRequiredService<IElevationProvider>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.ReadAsync([AtPixelEdge(128, 1)], new CancellationToken(canceled: true)));
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => provider.Source);

        Assert.Equal("database", error.Data["Field"]);
        Assert.IsType<SqliteException>(error.InnerException);
    }

    [Fact]
    public async Task ReplacingThePackageCannotSampleNewBlocksWithOldMetadata()
    {
        using TemporaryDirectory directory = new();
        using TemporaryDirectory replacement = new();
        CreatePackage(directory.DirectoryPath);
        CreatePackage(replacement.DirectoryPath, sourceVersion: "next-release");
        using ServiceProvider services = CreateServices(directory.DirectoryPath);
        IElevationProvider provider = services.GetRequiredService<IElevationProvider>();
        Assert.Equal("fixture-release", provider.Source?.Version);

        File.Move(Path.Combine(replacement.DirectoryPath, "elevation.sqlite"),
            Path.Combine(directory.DirectoryPath, "elevation.sqlite"), overwrite: true);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.ReadAsync([AtPixelEdge(128, 1)], default));

        Assert.Equal("metadata.json", error.Data["Field"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChecksumDetectsChangesEvenWhenTheDatabaseAndHeightsAreReadable(bool verifiedBeforeReplacement)
    {
        using TemporaryDirectory directory = new();
        CreatePackage(directory.DirectoryPath);
        string path = Path.Combine(directory.DirectoryPath, "elevation.sqlite");
        File.WriteAllText(path + ".sha256", PackageDigest(path));
        using ServiceProvider services = CreateServices(directory.DirectoryPath);
        IElevationProvider provider = services.GetRequiredService<IElevationProvider>();
        if (verifiedBeforeReplacement)
        {
            Assert.Equal(270, Assert.Single(await provider.ReadAsync([AtPixelEdge(128, 1)], default)));
        }

        using TemporaryDirectory replacement = new();
        CreatePackage(replacement.DirectoryPath);
        string changed = Path.Combine(replacement.DirectoryPath, "elevation.sqlite");
        ZeroBlock(changed);
        DateTime originalWriteTime = File.GetLastWriteTimeUtc(path);
        long originalLength = new FileInfo(path).Length;
        File.SetLastWriteTimeUtc(changed, originalWriteTime);
        File.Move(changed, path, overwrite: true);
        Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(path));
        Assert.Equal(originalLength, new FileInfo(path).Length);

        Assert.Equal("fixture-release", provider.Source?.Version);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.ReadAsync([AtPixelEdge(128, 1)], default));

        Assert.Equal("elevation.sqlite.sha256", error.Data["Field"]);
    }

    [Fact]
    public async Task HeightsComeFromTheSameOpenedPackageBytesThatPassedChecksum()
    {
        using TemporaryDirectory original = new();
        using TemporaryDirectory repair = new();
        using TemporaryDirectory snapshots = new();
        CreatePackage(original.DirectoryPath);
        CreatePackage(repair.DirectoryPath);
        string damaged = Path.Combine(original.DirectoryPath, "elevation.sqlite");
        string repaired = Path.Combine(repair.DirectoryPath, "elevation.sqlite");
        ZeroBlock(damaged);
        File.WriteAllText(damaged + ".sha256", PackageDigest(repaired));
        // Model atomic repair at the point the database byte stream is opened. SQLite
        // may prohibit renaming an open database on some platforms.
        SqliteElevationProvider provider = new(original.DirectoryPath, _ => File.OpenRead(repaired), snapshots.DirectoryPath);
        Assert.Equal("fixture-release", provider.Source?.Version);

        IReadOnlyList<double?> heights = await provider.ReadAsync([AtPixelEdge(128, 1)], default);

        Assert.Equal(270, Assert.Single(heights));
        Assert.Empty(Directory.EnumerateFileSystemEntries(snapshots.DirectoryPath));
    }

    [Fact]
    public async Task ChecksumAddedAfterReadingAnUnverifiedPackageIsEnforced()
    {
        using TemporaryDirectory directory = new();
        CreatePackage(directory.DirectoryPath);
        using ServiceProvider services = CreateServices(directory.DirectoryPath);
        IElevationProvider provider = services.GetRequiredService<IElevationProvider>();
        GeoCoordinate[] positions = [AtPixelEdge(128, 1)];
        Assert.Equal(270, Assert.Single(await provider.ReadAsync(positions, default)));

        string checksumPath = Path.Combine(directory.DirectoryPath, "elevation.sqlite.sha256");
        File.WriteAllText(checksumPath, new string('0', 64));

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.ReadAsync(positions, default));

        Assert.Equal("elevation.sqlite.sha256", error.Data["Field"]);
    }

    [Fact]
    public async Task CancellationDuringChecksumReadingAllowsVerificationToRetry()
    {
        using TemporaryDirectory directory = new();
        CreatePackage(directory.DirectoryPath);
        string path = Path.Combine(directory.DirectoryPath, "elevation.sqlite");
        string digest = PackageDigest(path);
        File.WriteAllText(path + ".sha256", digest);
        using TemporaryDirectory snapshots = new();
        using CancellationTokenSource calculation = new();
        bool cancelDuringFirstRead = true;
        SqliteElevationProvider provider = new(directory.DirectoryPath, packagePath =>
        {
            if (!cancelDuringFirstRead)
            {
                return File.OpenRead(packagePath);
            }

            cancelDuringFirstRead = false;
            return new CancellingChecksumStream(File.ReadAllBytes(packagePath), calculation);
        }, snapshots.DirectoryPath);
        GeoCoordinate[] positions = [AtPixelEdge(128, 1)];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ReadAsync(positions, calculation.Token));
        Assert.False(cancelDuringFirstRead);
        Assert.Empty(Directory.EnumerateFileSystemEntries(snapshots.DirectoryPath));
        File.WriteAllText(path + ".sha256", new string('0', 64));
        InvalidDataException invalidChecksum = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.ReadAsync(positions, default));
        Assert.Equal("elevation.sqlite.sha256", invalidChecksum.Data["Field"]);
        Assert.Empty(Directory.EnumerateFileSystemEntries(snapshots.DirectoryPath));

        File.WriteAllText(path + ".sha256", digest);

        Assert.Equal(270, Assert.Single(await provider.ReadAsync(positions, default)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(snapshots.DirectoryPath));
    }

    private static void ZeroBlock(string path)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE elevation_blocks SET samples = zeroblob(length(samples))
            WHERE grid_id = 1 AND block_column = 1
            """;
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static string PackageDigest(string path)
    {
        using FileStream input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private sealed class CancellingChecksumStream(byte[] data, CancellationTokenSource cancellation) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cancellation.IsCancellationRequested)
            {
                throw new InvalidOperationException("The checksum requested more bytes after cancellation without passing the token.");
            }

            if (Position > 0)
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return ValueTask.FromResult(Read(buffer.Span[..Math.Min(buffer.Length, 4096)]));
        }
    }

    private static ServiceProvider CreateServices(string directory, string? snapshotDirectory = null) => new ServiceCollection()
        .AddPersistence(directory, Path.Combine(directory, "settings.json"), snapshotDirectory)
        .BuildServiceProvider();

    private static GeoCoordinate AtPixelEdge(double column, double row) =>
        new(54 - row * LatitudeStep, 27 + column * LongitudeStep);

    private static void CreatePackage(
        string directory,
        bool noData = false,
        string coordinateSystem = "EPSG:4326",
        double rotation = 0,
        bool omitBlock = false,
        string sourceVersion = "fixture-release",
        bool latitudeBandSeam = false)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "elevation.sqlite"),
            Pooling = false
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE metadata (id INTEGER PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE elevation_blocks (
                grid_id INTEGER NOT NULL,
                block_row INTEGER NOT NULL,
                block_column INTEGER NOT NULL,
                samples BLOB NOT NULL,
                PRIMARY KEY (grid_id, block_row, block_column)
            ) WITHOUT ROWID;
            INSERT INTO metadata VALUES (1, $metadata);
            """;
        command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(new
        {
            schema_version = 1,
            coordinate_system = coordinateSystem,
            block_size = BlockSize,
            source_name = "Test DEM",
            source_version = sourceVersion,
            vertical_datum = "EGM2008",
            resolution_metres = 30,
            attribution = "Test attribution",
            license_url = "https://example.test/license",
            grids = latitudeBandSeam
                ? [
                    Grid(1, 3, 27 + 0.5 * LongitudeStep, 0),
                    new GridMetadata(2, 3, 2,
                        [1.5 * LongitudeStep, 0, 27 - 0.75 * LongitudeStep, 0, -LatitudeStep, 54 - 2 * LatitudeStep],
                        "lower.tif", new string('b', 64))
                ]
                : new[] { Grid(1, 130, 27, rotation), Grid(2, 2, 27 + 130 * LongitudeStep, 0) }
        }));
        command.ExecuteNonQuery();

        if (latitudeBandSeam)
        {
            InsertBlock(connection, grid: 1, blockColumn: 0, baseHeight: 0, profile: _ => 0);
            InsertBlock(connection, grid: 2, blockColumn: 0, baseHeight: 0, profile: column => column == 1 ? 90 : 0);
            return;
        }

        InsertBlock(connection, grid: 1, blockColumn: 0, baseHeight: 10);
        if (!omitBlock)
        {
            InsertBlock(connection, grid: 1, blockColumn: 1, baseHeight: 266, noData);
        }
        InsertBlock(connection, grid: 2, blockColumn: 0, baseHeight: 270);
    }

    private sealed record GridMetadata(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("width")] int Width,
        [property: JsonPropertyName("height")] int Height,
        [property: JsonPropertyName("affine")] double[] Affine,
        [property: JsonPropertyName("input_file")] string InputFile,
        [property: JsonPropertyName("input_sha256")] string InputSha256);

    private static GridMetadata Grid(int id, int width, double west, double rotation) =>
        new(id, width, 2, [LongitudeStep, rotation, west, 0, -LatitudeStep, 54], "fixture.tif", new string('a', 64));

    private static void InsertBlock(
        SqliteConnection connection, int grid, int blockColumn, int baseHeight, bool noData = false,
        Func<int, float>? profile = null)
    {
        byte[] samples = new byte[BlockSize * BlockSize * sizeof(float)];
        for (int row = 0; row < BlockSize; row++)
        {
            for (int column = 0; column < BlockSize; column++)
            {
                float height = row == 1 && column == 0 && noData
                    ? float.NaN
                    : profile?.Invoke(column) ?? baseHeight + 10 * row + 2 * column;
                int offset = (row * BlockSize + column) * sizeof(float);
                BinaryPrimitives.WriteInt32LittleEndian(samples.AsSpan(offset), BitConverter.SingleToInt32Bits(height));
            }
        }
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO elevation_blocks VALUES ($grid, 0, $column, $samples)";
        command.Parameters.AddWithValue("$grid", grid);
        command.Parameters.AddWithValue("$column", blockColumn);
        command.Parameters.AddWithValue("$samples", samples);
        command.ExecuteNonQuery();
    }
}
