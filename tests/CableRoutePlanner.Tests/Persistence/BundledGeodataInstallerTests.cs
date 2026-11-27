using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class BundledGeodataInstallerTests
{
    private static readonly IProgress<string> Progress = new InlineProgress(_ => { });

    [Theory]
    [InlineData(0)]
    [InlineData(17 * 1024 * 1024)]
    public async Task FragmentedAssetsKeepDisplayedProgressMonotonicAndPublishReadablePackage(int payloadSizeBytes)
    {
        using TemporaryDirectory directory = new();
        using SqliteGeodataFixture source = new();
        if (payloadSizeBytes > 0)
        {
            using SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(source.DirectoryPath, "geodata.sqlite"),
                Pooling = false
            }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE install_payload (content BLOB); INSERT INTO install_payload VALUES (zeroblob($size))";
            command.Parameters.AddWithValue("$size", payloadSizeBytes);
            command.ExecuteNonQuery();
        }
        byte[] expected = ReadDatabase(source);
        JsonNode manifestNode = JsonNode.Parse(ReadManifest(source))!;
        manifestNode["database_sha256"] = Convert.ToHexString(SHA256.HashData(expected));
        string manifest = manifestNode.ToJsonString();
        List<string> messages = [];
        await BundledGeodataInstaller.InstallAsync(directory.DirectoryPath, manifest, ["geodata.sqlite"],
            _ => new FragmentedStream(expected), new InlineProgress(messages.Add), default);
        string[] displayedProgress = messages.Where(message => message.StartsWith("Подготовка карты:", StringComparison.Ordinal)).ToArray();
        string[] expectedProgress = payloadSizeBytes == 0 ? [] : ["Подготовка карты: 16 МБ…"];
        Assert.Equal(expectedProgress, displayedProgress);
        await AssertPackageAsync(directory.DirectoryPath, expected, manifest);
        AssertNoTemporaryFiles(directory.DirectoryPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlainAndGzipAssetsPublishReadablePackages(bool compressed)
    {
        using TemporaryDirectory directory = new();
        using SqliteGeodataFixture source = new();
        byte[] expected = ReadDatabase(source);
        string manifest = ReadManifest(source);
        byte[] asset = compressed ? Compress(expected) : expected;
        string name = compressed ? "geodata.sqlite.gz" : "geodata.sqlite";
        await BundledGeodataInstaller.InstallAsync(directory.DirectoryPath, manifest, [name],
            _ => new MemoryStream(asset), Progress, default);
        await AssertPackageAsync(directory.DirectoryPath, expected, manifest);
        AssertNoTemporaryFiles(directory.DirectoryPath);
    }

    [Fact]
    public async Task MatchingHealthyPackageNeedsNoRemainingAssets()
    {
        using SqliteGeodataFixture installed = new();
        byte[] expected = ReadDatabase(installed);
        string manifest = ReadManifest(installed);
        await BundledGeodataInstaller.InstallAsync(installed.DirectoryPath, manifest, [], File.OpenRead, Progress, default);
        await AssertPackageAsync(installed.DirectoryPath, expected, manifest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingManifestRepairsMissingOrCorruptDatabase(bool missing)
    {
        using SqliteGeodataFixture installed = new();
        byte[] expected = ReadDatabase(installed);
        string manifest = ReadManifest(installed);
        string database = Path.Combine(installed.DirectoryPath, "geodata.sqlite");
        if (missing)
        {
            File.Delete(database);
        }
        else
        {
            await File.WriteAllBytesAsync(database, [1, 2, 3]);
        }
        await InstallBytesAsync(installed.DirectoryPath, expected, manifest);
        await AssertPackageAsync(installed.DirectoryPath, expected, manifest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptChunkPreservesReadablePackageAndRetryRepairsIt(bool corrupt)
    {
        using SqliteGeodataFixture installed = new();
        using SqliteGeodataFixture source = NewPackage();
        byte[] oldDatabase = ReadDatabase(installed);
        string oldManifest = ReadManifest(installed);
        byte[] expected = ReadDatabase(source);
        string manifest = ReadManifest(source);
        int split = expected.Length / 3;
        Dictionary<string, byte[]> chunks = new(StringComparer.Ordinal)
        {
            ["geodata.0000.gz"] = Compress(expected[..split]),
            ["geodata.0001.gz"] = Compress(expected[split..(2 * split)]),
            ["geodata.0002.gz"] = Compress(expected[(2 * split)..])
        };
        if (corrupt)
        {
            chunks["geodata.0001.gz"] = Compress(new byte[split]);
        }
        else
        {
            chunks.Remove("geodata.0001.gz");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => BundledGeodataInstaller.InstallAsync(
            installed.DirectoryPath, manifest, chunks.Keys.ToArray(),
            name => new MemoryStream(chunks[name]), Progress, default));
        await AssertPackageAsync(installed.DirectoryPath, oldDatabase, oldManifest);
        AssertNoTemporaryFiles(installed.DirectoryPath);
        chunks["geodata.0001.gz"] = Compress(expected[split..(2 * split)]);
        await BundledGeodataInstaller.InstallAsync(installed.DirectoryPath, manifest,
            chunks.Keys.ToArray(), name => new MemoryStream(chunks[name]), Progress, default);
        await AssertPackageAsync(installed.DirectoryPath, expected, manifest);
    }

    [Fact]
    public async Task InvalidGzipPreservesReadablePackage()
    {
        using SqliteGeodataFixture installed = new();
        using SqliteGeodataFixture source = NewPackage();
        byte[] expected = ReadDatabase(installed);
        string manifest = ReadManifest(installed);
        await Assert.ThrowsAsync<InvalidDataException>(() => BundledGeodataInstaller.InstallAsync(
            installed.DirectoryPath, ReadManifest(source), ["geodata.0000.gz"],
            _ => new MemoryStream([1, 2, 3]), Progress, default));
        await AssertPackageAsync(installed.DirectoryPath, expected, manifest);
        AssertNoTemporaryFiles(installed.DirectoryPath);
    }

    [Fact]
    public async Task MatchingChecksumDoesNotPermitInvalidSqlite()
    {
        using SqliteGeodataFixture installed = new();
        byte[] expected = ReadDatabase(installed);
        string oldManifest = ReadManifest(installed);
        byte[] invalid = new byte[expected.Length];
        string manifest = JsonSerializer.Serialize(new { database_sha256 = Convert.ToHexString(SHA256.HashData(invalid)) });
        await Assert.ThrowsAsync<InvalidDataException>(() => InstallBytesAsync(installed.DirectoryPath, invalid, manifest));
        await AssertPackageAsync(installed.DirectoryPath, expected, oldManifest);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public async Task InvalidManifestChecksumPreservesReadablePackage(string? checksum)
    {
        using SqliteGeodataFixture installed = new();
        using SqliteGeodataFixture source = NewPackage();
        byte[] expected = ReadDatabase(installed);
        string oldManifest = ReadManifest(installed);
        string manifest = JsonSerializer.Serialize(new { database_sha256 = checksum });
        await Assert.ThrowsAsync<InvalidDataException>(() => InstallBytesAsync(
            installed.DirectoryPath, ReadDatabase(source), manifest));
        await AssertPackageAsync(installed.DirectoryPath, expected, oldManifest);
        AssertNoTemporaryFiles(installed.DirectoryPath);
    }

    [Fact]
    public async Task CancellationDuringExtractionPreservesReadablePackage()
    {
        using SqliteGeodataFixture installed = new();
        using SqliteGeodataFixture source = NewPackage();
        using CancellationTokenSource cancellation = new();
        byte[] expected = ReadDatabase(installed);
        string manifest = ReadManifest(installed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BundledGeodataInstaller.InstallAsync(
            installed.DirectoryPath, ReadManifest(source), ["geodata.sqlite"], _ =>
            {
                cancellation.Cancel();
                return new MemoryStream(ReadDatabase(source));
            }, Progress, cancellation.Token));
        await AssertPackageAsync(installed.DirectoryPath, expected, manifest);
        AssertNoTemporaryFiles(installed.DirectoryPath);
    }

    [Fact]
    public async Task CancellationBeforePublicationPreservesReadablePackage()
    {
        using SqliteGeodataFixture installed = new();
        using SqliteGeodataFixture source = NewPackage();
        using CancellationTokenSource cancellation = new();
        byte[] expected = ReadDatabase(installed);
        string manifest = ReadManifest(installed);
        IProgress<string> progress = new InlineProgress(message =>
        {
            if (message == "Публикация карты…")
            {
                cancellation.Cancel();
            }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InstallBytesAsync(
            installed.DirectoryPath, ReadDatabase(source), ReadManifest(source), progress, cancellation.Token));
        await AssertPackageAsync(installed.DirectoryPath, expected, manifest);
        AssertNoTemporaryFiles(installed.DirectoryPath);
    }

    [Fact]
    public async Task ParallelInstallationsAndCancelledWaiterDoNotShareTemporaryFiles()
    {
        using SqliteGeodataFixture installed = new();
        using SqliteGeodataFixture source = NewPackage();
        using SqliteGeodataFixture subsequent = new([], null, regionId: "subsequent", schemaVersion: 1);
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] expected = ReadDatabase(source);
        string manifest = ReadManifest(source);
        Task first = BundledGeodataInstaller.InstallAsync(installed.DirectoryPath, manifest, ["geodata.sqlite"],
            _ => new GatedStream(expected, entered, release.Task), Progress, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Task second = InstallBytesAsync(installed.DirectoryPath, ReadDatabase(subsequent), ReadManifest(subsequent));
        Task cancelled = InstallBytesAsync(installed.DirectoryPath, expected, manifest, cancellationToken: cancellation.Token);
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, second);
        }
        await AssertPackageAsync(installed.DirectoryPath, ReadDatabase(subsequent), ReadManifest(subsequent));
        AssertNoTemporaryFiles(installed.DirectoryPath);
    }

    [Fact]
    public async Task FailedPublicationKeepsTheInstalledPackageReadable()
    {
        using SqliteGeodataFixture installed = new();
        using SqliteGeodataFixture source = NewPackage();
        byte[] expected = ReadDatabase(installed);
        string manifest = ReadManifest(installed);
        string manifestPath = Path.Combine(installed.DirectoryPath, "manifest.json");
        IProgress<string> progress = new InlineProgress(message =>
        {
            if (message == "Публикация карты…")
            {
                if (OperatingSystem.IsWindows())
                {
                    File.SetAttributes(manifestPath, FileAttributes.ReadOnly);
                }
                else
                {
                    File.SetUnixFileMode(installed.DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                }
            }
        });
        try
        {
            Exception? error = await Record.ExceptionAsync(() => InstallBytesAsync(
                installed.DirectoryPath, ReadDatabase(source), ReadManifest(source), progress));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(manifestPath, FileAttributes.Normal);
            }
            else
            {
                File.SetUnixFileMode(installed.DirectoryPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        await AssertPackageAsync(installed.DirectoryPath, expected, manifest);
    }

    [Fact]
    public async Task InstallationDoesNotDeleteUnownedTemporaryFiles()
    {
        using SqliteGeodataFixture source = NewPackage();
        using TemporaryDirectory directory = new();
        string foreignFile = Path.Combine(directory.DirectoryPath, "geodata.sqlite.tmp");
        await File.WriteAllTextAsync(foreignFile, "owned by another operation");
        string foreignDirectory = Path.Combine(directory.DirectoryPath, ".geodata-install-foreign");
        Directory.CreateDirectory(foreignDirectory);
        await InstallBytesAsync(directory.DirectoryPath, ReadDatabase(source), ReadManifest(source));
        Assert.Equal("owned by another operation", await File.ReadAllTextAsync(foreignFile));
        Assert.True(Directory.Exists(foreignDirectory));
        await AssertPackageAsync(directory.DirectoryPath, ReadDatabase(source), ReadManifest(source));
    }

    [Fact]
    public async Task ManifestPublicationFailureRestoresPreviousDatabase()
    {
        using SqliteGeodataFixture installed = new();
        using SqliteGeodataFixture source = NewPackage();
        byte[] expected = ReadDatabase(installed);
        string manifestPath = Path.Combine(installed.DirectoryPath, "manifest.json");
        // A directory occupying the destination is a portable publication failure.
        File.Delete(manifestPath);
        Directory.CreateDirectory(manifestPath);
        Exception? error = await Record.ExceptionAsync(() => InstallBytesAsync(
            installed.DirectoryPath, ReadDatabase(source), ReadManifest(source)));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(installed.DirectoryPath, "geodata.sqlite")));
        AssertDatabaseReadable(installed.DirectoryPath);
        AssertNoTemporaryFiles(installed.DirectoryPath);
    }

    private static SqliteGeodataFixture NewPackage() => new([], null, regionId: "updated-map", schemaVersion: 3);

    private static Task InstallBytesAsync(string directory, byte[] bytes, string manifest,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        BundledGeodataInstaller.InstallAsync(directory, manifest, ["geodata.sqlite"],
            _ => new MemoryStream(bytes), progress ?? Progress, cancellationToken);

    private static byte[] ReadDatabase(SqliteGeodataFixture fixture) =>
        File.ReadAllBytes(Path.Combine(fixture.DirectoryPath, "geodata.sqlite"));

    private static string ReadManifest(SqliteGeodataFixture fixture) =>
        File.ReadAllText(Path.Combine(fixture.DirectoryPath, "manifest.json"));

    private static async Task AssertPackageAsync(string directory, byte[] expected, string manifest)
    {
        Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(directory, "geodata.sqlite")));
        Assert.Equal(manifest, await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json")));
        SqliteGeodataRepository repository = new(directory);
        Assert.Empty(await repository.QueryAsync(new GeoBounds(27, 54, 27.006, 54.006), default));
        AssertDatabaseReadable(directory);
    }

    private static void AssertDatabaseReadable(string directory)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "geodata.sqlite"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", command.ExecuteScalar());
    }

    private static void AssertNoTemporaryFiles(string directory)
    {
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Assert.Empty(Directory.GetDirectories(directory, ".geodata-install-*"));
    }

    private static byte[] Compress(byte[] bytes)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(bytes);
        }
        return output.ToArray();
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 4093)], cancellationToken);
    }

    private sealed class GatedStream(byte[] bytes, TaskCompletionSource entered, Task release) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await release.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
