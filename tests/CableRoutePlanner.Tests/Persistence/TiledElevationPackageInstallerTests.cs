using System.Text;
using CableRoutePlanner.Persistence;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class TiledElevationPackageInstallerTests
{
    [Fact]
    public async Task MissingLaterBundledAssetRaisesStructuredErrorWithoutPublishingPartialCountry()
    {
        using TemporaryDirectory assets = new();
        using TemporaryDirectory installed = new();
        TiledElevationFixture.Create(assets.DirectoryPath);
        File.Delete(Path.Combine(assets.DirectoryPath, "elevation", "east", "elevation.sqlite"));

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Install(assets.DirectoryPath, installed.DirectoryPath));

        Assert.Equal("tiles.asset", error.Data["Field"]);
        Assert.Equal("east/elevation.sqlite", error.Data["Value"]);
        Assert.IsType<FileNotFoundException>(error.InnerException);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(installed.DirectoryPath, "elevation")));
    }

    [Fact]
    public async Task MissingShardInPublishedGenerationIsRestoredOnNextStartup()
    {
        using TemporaryDirectory assets = new();
        using TemporaryDirectory installed = new();
        TiledElevationFixture.Create(assets.DirectoryPath);
        await Install(assets.DirectoryPath, installed.DirectoryPath);
        string shard = Assert.Single(Directory.EnumerateFiles(Path.Combine(installed.DirectoryPath, "elevation"),
            "elevation.sqlite", SearchOption.AllDirectories), path => Path.GetFileName(Path.GetDirectoryName(path)) == "east");
        File.Delete(shard);

        await Install(assets.DirectoryPath, installed.DirectoryPath);

        Assert.Equal(11, Assert.Single(await new TiledElevationProvider(installed.DirectoryPath)
            .ReadAsync([new(53.125, 27.375)], default)));
    }

    [Fact]
    public async Task CompleteCountryInstallsIndependentlyOfMapAndUnchangedStartupDoesNotCopyDatabases()
    {
        using TemporaryDirectory assets = new();
        using TemporaryDirectory installed = new();
        TiledElevationFixture.Create(assets.DirectoryPath, compressed: true);
        string map = Path.Combine(installed.DirectoryPath, "manifest.json");
        File.WriteAllText(map, "same OSM");
        await Install(assets.DirectoryPath, installed.DirectoryPath);
        TiledElevationProvider provider = new(installed.DirectoryPath);
        Assert.Equal(new double?[] { 7, 11 }, await provider.ReadAsync([new(53.125, 27.125), new(53.125, 27.375)], default));

        await TiledElevationPackageInstaller.InstallAsync(installed.DirectoryPath,
            TiledElevationFixture.Catalog(), path =>
            {
                Assert.EndsWith(".sha256", path, StringComparison.Ordinal);
                return File.OpenRead(Path.Combine(assets.DirectoryPath, "elevation", path));
            }, default);

        Assert.Equal("same OSM", File.ReadAllText(map));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(Path.Combine(installed.DirectoryPath, "elevation")),
            entry => Path.GetFileName(entry).StartsWith(".staging-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangedHeightsWithIdenticalCatalogPublishNewGenerationAndKeepExistingReaderSnapshotValid()
    {
        using TemporaryDirectory first = new();
        using TemporaryDirectory second = new();
        using TemporaryDirectory installed = new();
        TiledElevationFixture.Create(first.DirectoryPath);
        TiledElevationFixture.Create(second.DirectoryPath, heightOffset: 100);
        await Install(first.DirectoryPath, installed.DirectoryPath);
        TiledElevationProvider previous = new(installed.DirectoryPath);
        Assert.Equal(7, Assert.Single(await previous.ReadAsync([new(53.125, 27.125)], default)));

        await Install(second.DirectoryPath, installed.DirectoryPath);

        Assert.Equal(7, Assert.Single(await previous.ReadAsync([new(53.125, 27.125)], default)));
        Assert.Equal(107, Assert.Single(await new TiledElevationProvider(installed.DirectoryPath)
            .ReadAsync([new(53.125, 27.125)], default)));
    }

    [Fact]
    public async Task CorruptLaterShardLeavesPreviousCountryCompletelyReadableAndCleansStaging()
    {
        using TemporaryDirectory first = new();
        using TemporaryDirectory revised = new();
        using TemporaryDirectory installed = new();
        TiledElevationFixture.Create(first.DirectoryPath);
        TiledElevationFixture.Create(revised.DirectoryPath, version: "new", heightOffset: 100);
        await Install(first.DirectoryPath, installed.DirectoryPath);
        string index = File.ReadAllText(Path.Combine(installed.DirectoryPath, "elevation", "index.json"));
        File.WriteAllText(Path.Combine(revised.DirectoryPath, "elevation", "east", "elevation.sqlite"), "corrupt");

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Install(revised.DirectoryPath, installed.DirectoryPath));

        Assert.NotNull(error.Data["ExpectedSha256"]);
        Assert.NotNull(error.Data["ActualSha256"]);
        Assert.Equal(index, File.ReadAllText(Path.Combine(installed.DirectoryPath, "elevation", "index.json")));
        Assert.Equal(new double?[] { 7, 11 }, await new TiledElevationProvider(installed.DirectoryPath)
            .ReadAsync([new(53.125, 27.125), new(53.125, 27.375)], default));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(Path.Combine(installed.DirectoryPath, "elevation")),
            entry => Path.GetFileName(entry).StartsWith(".staging-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationDuringStreamingDoesNotPublishPartialCountryAndDisposesAsset()
    {
        using TemporaryDirectory assets = new();
        using TemporaryDirectory installed = new();
        TiledElevationFixture.Create(assets.DirectoryPath);
        using CancellationTokenSource cancellation = new();
        CancellingStream? opened = null;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TiledElevationPackageInstaller.InstallAsync(installed.DirectoryPath, TiledElevationFixture.Catalog(), path =>
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(assets.DirectoryPath, "elevation", path));
                if (path.EndsWith(".sha256", StringComparison.Ordinal))
                {
                    return new MemoryStream(bytes);
                }
                opened = new(bytes, cancellation);
                return opened;
            }, cancellation.Token));

        Assert.True(opened?.Disposed);
        Assert.False(File.Exists(Path.Combine(installed.DirectoryPath, "elevation", "index.json")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(installed.DirectoryPath, "elevation")));
    }

    [Fact]
    public async Task InvalidCatalogAndPreCancellationOpenNoAssetsOrDirectories()
    {
        using TemporaryDirectory installed = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TiledElevationPackageInstaller.InstallAsync(installed.DirectoryPath, "invalid", _ =>
                throw new InvalidOperationException("Asset must remain unopened"), new CancellationToken(canceled: true)));
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            TiledElevationPackageInstaller.InstallAsync(installed.DirectoryPath, TiledElevationFixture.Catalog("../escape"),
                _ => throw new InvalidOperationException("Asset must remain unopened"), default));
        Assert.Equal("tiles[0].directory", error.Data["Field"]);
        Assert.Empty(Directory.EnumerateFileSystemEntries(installed.DirectoryPath));
    }

    [Fact]
    public async Task InvalidChecksumAndContradictorySourceCannotPublishCountry()
    {
        using TemporaryDirectory assets = new();
        using TemporaryDirectory installed = new();
        TiledElevationFixture.Create(assets.DirectoryPath);
        InvalidDataException checksum = await Assert.ThrowsAsync<InvalidDataException>(() =>
            TiledElevationPackageInstaller.InstallAsync(installed.DirectoryPath, TiledElevationFixture.Catalog(),
                _ => new MemoryStream(Encoding.UTF8.GetBytes("invalid")), default));
        Assert.Equal("elevation.sqlite.sha256", checksum.Data["Field"]);
        InvalidDataException source = await Assert.ThrowsAsync<InvalidDataException>(() =>
            TiledElevationPackageInstaller.InstallAsync(installed.DirectoryPath, TiledElevationFixture.Catalog(version: "wrong"),
                path => File.OpenRead(Path.Combine(assets.DirectoryPath, "elevation", path)), default));
        Assert.Equal("tiles.source", source.Data["Field"]);
        Assert.False(File.Exists(Path.Combine(installed.DirectoryPath, "elevation", "index.json")));
    }

    [Fact]
    public async Task RemovingBundlePreservesUnmanagedCountryAndDeletesOnlyOwnedCatalog()
    {
        using TemporaryDirectory assets = new();
        using TemporaryDirectory installed = new();
        TiledElevationFixture.Create(assets.DirectoryPath);
        TiledElevationPackageInstaller.RemoveInstalledPackage(assets.DirectoryPath, default);
        Assert.Equal(7, Assert.Single(await new TiledElevationProvider(assets.DirectoryPath).ReadAsync([new(53.125, 27.125)], default)));
        await Install(assets.DirectoryPath, installed.DirectoryPath);

        TiledElevationPackageInstaller.RemoveInstalledPackage(installed.DirectoryPath, default);

        Assert.Null(new TiledElevationProvider(installed.DirectoryPath).Source);
        Assert.True(Directory.Exists(Path.Combine(installed.DirectoryPath, "elevation", ".bundled")));
        await Install(assets.DirectoryPath, installed.DirectoryPath);
        File.WriteAllText(Path.Combine(installed.DirectoryPath, "elevation", "index.json"), TiledElevationFixture.Catalog());
        TiledElevationPackageInstaller.RemoveInstalledPackage(installed.DirectoryPath, default);
        Assert.Equal(TiledElevationFixture.Catalog(), File.ReadAllText(Path.Combine(installed.DirectoryPath, "elevation", "index.json")));
    }

    private static Task Install(string assets, string installed) => TiledElevationPackageInstaller.InstallAsync(installed,
        File.ReadAllText(Path.Combine(assets, "elevation", "index.json")),
        path => File.OpenRead(Path.Combine(assets, "elevation", path)), default);

    private sealed class CancellingStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return base.ReadAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
