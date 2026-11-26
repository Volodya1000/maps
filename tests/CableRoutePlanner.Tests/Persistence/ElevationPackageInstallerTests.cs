using System.Security.Cryptography;
using System.Text;
using CableRoutePlanner.Persistence;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class ElevationPackageInstallerTests
{
    [Fact]
    public async Task ChangedElevationIsInstalledEvenWhenMapIdentityDoesNotChange()
    {
        using TemporaryDirectory directory = new();
        string mapManifest = Path.Combine(directory.DirectoryPath, "manifest.json");
        await File.WriteAllTextAsync(mapManifest, "same-map");
        byte[] first = Encoding.UTF8.GetBytes("first elevation");
        byte[] second = Encoding.UTF8.GetBytes("revised elevation");

        await ElevationPackageInstaller.InstallAsync(directory.DirectoryPath, Hash(first), () => new MemoryStream(first), default);
        await ElevationPackageInstaller.InstallAsync(directory.DirectoryPath, Hash(second), () => new MemoryStream(second), default);

        Assert.Equal(second, await File.ReadAllBytesAsync(Path.Combine(directory.DirectoryPath, "elevation.sqlite")));
        Assert.Equal("same-map", await File.ReadAllTextAsync(mapManifest));
        Assert.Equal(Hash(second), await File.ReadAllTextAsync(Path.Combine(directory.DirectoryPath, "elevation.sqlite.sha256")));
        Assert.Empty(Directory.GetFiles(directory.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task FailedChecksumPreservesPreviouslyInstalledDataAndIdentity()
    {
        using TemporaryDirectory directory = new();
        byte[] original = Encoding.UTF8.GetBytes("verified");
        byte[] damaged = Encoding.UTF8.GetBytes("damaged");
        byte[] expected = Encoding.UTF8.GetBytes("expected new package");
        await ElevationPackageInstaller.InstallAsync(directory.DirectoryPath, Hash(original), () => new MemoryStream(original), default);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ElevationPackageInstaller.InstallAsync(directory.DirectoryPath, Hash(expected), () => new MemoryStream(damaged), default));

        Assert.Equal(Hash(expected), error.Data["ExpectedSha256"]);
        Assert.Equal(Hash(damaged), error.Data["ActualSha256"]);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(directory.DirectoryPath, "elevation.sqlite")));
        Assert.Equal(Hash(original), await File.ReadAllTextAsync(Path.Combine(directory.DirectoryPath, "elevation.sqlite.sha256")));
        Assert.Empty(Directory.GetFiles(directory.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task CancellationPreservesInstalledPackage()
    {
        using TemporaryDirectory directory = new();
        byte[] original = Encoding.UTF8.GetBytes("verified");
        await ElevationPackageInstaller.InstallAsync(directory.DirectoryPath, Hash(original), () => new MemoryStream(original), default);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ElevationPackageInstaller.InstallAsync(directory.DirectoryPath, Hash(Encoding.UTF8.GetBytes("other")), () => new MemoryStream(original), cancellation.Token));

        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(directory.DirectoryPath, "elevation.sqlite")));
        Assert.Empty(Directory.GetFiles(directory.DirectoryPath, "*.tmp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../bad")]
    [InlineData("ffffffff")]
    public async Task InvalidIdentityIsRejectedBeforeCreatingFiles(string identity)
    {
        using TemporaryDirectory directory = new();

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            ElevationPackageInstaller.InstallAsync(directory.DirectoryPath, identity, () => new MemoryStream(), default));

        Assert.Equal("expectedSha256", error.ParamName);
        Assert.Empty(Directory.GetFiles(directory.DirectoryPath));
    }

    [Fact]
    public async Task CorruptedInstalledBytesAreRepairedWhenTheIdentityMarkerStillMatches()
    {
        using TemporaryDirectory directory = new();
        byte[] verified = Encoding.UTF8.GetBytes("verified source");
        await ElevationPackageInstaller.InstallAsync(directory.DirectoryPath, Hash(verified), () => new MemoryStream(verified), default);
        string database = Path.Combine(directory.DirectoryPath, "elevation.sqlite");
        await File.WriteAllTextAsync(database, "truncated");

        await ElevationPackageInstaller.InstallAsync(directory.DirectoryPath, Hash(verified), () => new MemoryStream(verified), default);

        Assert.Equal(verified, await File.ReadAllBytesAsync(database));
        Assert.Equal(Hash(verified), await File.ReadAllTextAsync(database + ".sha256"));
        Assert.Empty(Directory.GetFiles(directory.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task RemovingBundledElevationRestoresMissingDemAndPreservesTheMap()
    {
        using TemporaryDirectory directory = new();
        byte[] verified = Encoding.UTF8.GetBytes("verified source");
        await ElevationPackageInstaller.InstallAsync(directory.DirectoryPath, Hash(verified), () => new MemoryStream(verified), default);
        await ElevationPackageInstaller.InstallAsync(directory.DirectoryPath, Hash(verified), () => new MemoryStream(verified), default);
        string map = Path.Combine(directory.DirectoryPath, "geodata.sqlite");
        await File.WriteAllTextAsync(map, "unchanged map");

        ElevationPackageInstaller.RemoveInstalledPackage(directory.DirectoryPath, default);

        Assert.False(File.Exists(Path.Combine(directory.DirectoryPath, "elevation.sqlite")));
        Assert.False(File.Exists(Path.Combine(directory.DirectoryPath, "elevation.sqlite.sha256")));
        Assert.Equal("unchanged map", await File.ReadAllTextAsync(map));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovingBundledElevationPreservesAnUnmanagedPackage(bool installIdenticalAsset)
    {
        using TemporaryDirectory directory = new();
        string database = Path.Combine(directory.DirectoryPath, "elevation.sqlite");
        string identity = database + ".sha256";
        string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Elevation");
        byte[] prepared = await File.ReadAllBytesAsync(Path.Combine(fixture, "elevation.sqlite"));
        byte[] checksum = await File.ReadAllBytesAsync(Path.Combine(fixture, "elevation.sqlite.sha256"));
        await File.WriteAllBytesAsync(database, prepared);
        await File.WriteAllBytesAsync(identity, checksum);
        if (installIdenticalAsset)
        {
            await ElevationPackageInstaller.InstallAsync(
                directory.DirectoryPath, Hash(prepared), () => new MemoryStream(prepared), default);
        }

        ElevationPackageInstaller.RemoveInstalledPackage(directory.DirectoryPath, default);

        Assert.True(File.Exists(database));
        Assert.True(File.Exists(identity));
        Assert.Equal(prepared, await File.ReadAllBytesAsync(database));
        Assert.Equal(checksum, await File.ReadAllBytesAsync(identity));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
