using System.IO.Compression;
using System.Text;
using CableRoutePlanner.Persistence;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class BundledGeodataInstallerTests
{
    private static readonly IProgress<string> Progress = new Progress<string>();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlainAndGzipAssetsPublishTheSameBytes(bool compressed)
    {
        using TemporaryDirectory directory = new();
        byte[] expected = Encoding.UTF8.GetBytes("offline map bytes");
        byte[] asset = compressed ? Compress(expected) : expected;
        string name = compressed ? "geodata.sqlite.gz" : "geodata.sqlite";
        await BundledGeodataInstaller.InstallAsync(directory.DirectoryPath, "manifest", [name],
            _ => new MemoryStream(asset), Progress, default);
        Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(directory.DirectoryPath, "geodata.sqlite")));
        Assert.Equal("manifest", await File.ReadAllTextAsync(Path.Combine(directory.DirectoryPath, "manifest.json")));
        Assert.False(File.Exists(Path.Combine(directory.DirectoryPath, "geodata.sqlite.tmp")));
    }

    [Fact]
    public async Task MatchingInstalledPackageNeedsNoRemainingAssets()
    {
        using TemporaryDirectory directory = new();
        string database = Path.Combine(directory.DirectoryPath, "geodata.sqlite");
        await File.WriteAllTextAsync(database, "existing map");
        await File.WriteAllTextAsync(Path.Combine(directory.DirectoryPath, "manifest.json"), "manifest");
        await BundledGeodataInstaller.InstallAsync(directory.DirectoryPath, "manifest", [],
            File.OpenRead, Progress, default);
        Assert.Equal("existing map", await File.ReadAllTextAsync(database));
    }

    [Fact]
    public async Task CorruptChunkPreservesPreviousPackageAndRemovesTemporaryFile()
    {
        using TemporaryDirectory directory = new();
        string database = Path.Combine(directory.DirectoryPath, "geodata.sqlite");
        string manifest = Path.Combine(directory.DirectoryPath, "manifest.json");
        await File.WriteAllTextAsync(database, "old map");
        await File.WriteAllTextAsync(manifest, "old manifest");
        await Assert.ThrowsAsync<InvalidDataException>(() => BundledGeodataInstaller.InstallAsync(
            directory.DirectoryPath, "new manifest", ["geodata.0000.gz"],
            _ => new MemoryStream(Encoding.UTF8.GetBytes("not gzip")), Progress, default));
        Assert.Equal("old map", await File.ReadAllTextAsync(database));
        Assert.Equal("old manifest", await File.ReadAllTextAsync(manifest));
        Assert.False(File.Exists(database + ".tmp"));
    }

    [Fact]
    public async Task CancellationDuringExtractionDoesNotPublishAPartialPackage()
    {
        using TemporaryDirectory directory = new();
        using CancellationTokenSource cancellation = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BundledGeodataInstaller.InstallAsync(
            directory.DirectoryPath, "manifest", ["geodata.sqlite"], _ =>
            {
                cancellation.Cancel();
                return new MemoryStream([1, 2, 3]);
            }, Progress, cancellation.Token));
        Assert.False(File.Exists(Path.Combine(directory.DirectoryPath, "manifest.json")));
        Assert.False(File.Exists(Path.Combine(directory.DirectoryPath, "geodata.sqlite")));
        Assert.False(File.Exists(Path.Combine(directory.DirectoryPath, "geodata.sqlite.tmp")));
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
}
