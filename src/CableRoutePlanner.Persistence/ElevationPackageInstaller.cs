using System.Security.Cryptography;

namespace CableRoutePlanner.Persistence;

/// <summary>Installs a verified optional DEM independently of the OSM package identity.</summary>
public static class ElevationPackageInstaller
{
    private const string OwnershipFileName = "elevation.bundled.sha256";

    public static void RemoveInstalledPackage(string directory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directory);
        cancellationToken.ThrowIfCancellationRequested();
        string ownership = Path.Combine(directory, OwnershipFileName);
        if (!File.Exists(ownership))
        {
            return;
        }

        string database = Path.Combine(directory, "elevation.sqlite");
        string identity = database + ".sha256";
        string installed = File.ReadAllText(ownership).Trim();
        if (File.Exists(identity) && string.Equals(installed, File.ReadAllText(identity).Trim(),
            StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(database);
            File.Delete(identity);
        }
        File.Delete(ownership);
    }

    public static async Task InstallAsync(
        string directory,
        string expectedSha256,
        Func<Stream> openDatabase,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("A data directory is required.", nameof(directory));
        }
        ArgumentNullException.ThrowIfNull(expectedSha256);
        ArgumentNullException.ThrowIfNull(openDatabase);
        string identity = expectedSha256.Trim().ToUpperInvariant();
        if (identity.Length != 64 || !identity.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Expected a 64-digit SHA-256 identity.", nameof(expectedSha256));
        }

        cancellationToken.ThrowIfCancellationRequested();
        string database = Path.Combine(directory, "elevation.sqlite");
        string identityPath = database + ".sha256";
        if (File.Exists(database) && File.Exists(identityPath)
            && string.Equals((await File.ReadAllTextAsync(identityPath, cancellationToken)).Trim(),
                identity, StringComparison.OrdinalIgnoreCase)
            && string.Equals(await ComputeInstalledHashAsync(database, cancellationToken), identity, StringComparison.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Ownership belongs to the app only after it publishes the package.
            return;
        }

        Directory.CreateDirectory(directory);
        string temporaryDatabase = Path.Combine(directory, "elevation-" + Guid.NewGuid().ToString("N") + ".tmp");
        string temporaryIdentity = temporaryDatabase + ".sha256.tmp";
        string temporaryOwnership = temporaryDatabase + ".bundled.tmp";
        try
        {
            string actual;
            using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using Stream input = openDatabase();
                await using FileStream output = File.Create(temporaryDatabase);
                byte[] buffer = new byte[128 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                actual = Convert.ToHexString(hash.GetHashAndReset());
            }

            if (!string.Equals(actual, identity, StringComparison.Ordinal))
            {
                InvalidDataException error = new("Elevation package SHA-256 does not match its identity.");
                error.Data["ExpectedSha256"] = identity;
                error.Data["ActualSha256"] = actual;
                throw error;
            }

            await File.WriteAllTextAsync(temporaryIdentity, identity, cancellationToken);
            await File.WriteAllTextAsync(temporaryOwnership, identity, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryDatabase, database, overwrite: true);
            File.Move(temporaryIdentity, identityPath, overwrite: true);
            File.Move(temporaryOwnership, Path.Combine(directory, OwnershipFileName), overwrite: true);
        }
        finally
        {
            File.Delete(temporaryDatabase);
            File.Delete(temporaryIdentity);
            File.Delete(temporaryOwnership);
        }
    }

    private static async Task<string> ComputeInstalledHashAsync(string database, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(database);
        using SHA256 hash = SHA256.Create();
        return Convert.ToHexString(await hash.ComputeHashAsync(stream, cancellationToken));
    }
}
