using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CableRoutePlanner.Persistence;

/// <summary>Publishes a complete immutable shard generation before replacing its catalog.</summary>
public static class TiledElevationPackageInstaller
{
    private const string OwnershipFileName = "index.bundled.sha256";

    public static async Task InstallAsync(string dataDirectory, string indexJson,
        Func<string, Stream> openAsset, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("A data directory is required.", nameof(dataDirectory));
        }
        ArgumentNullException.ThrowIfNull(indexJson);
        ArgumentNullException.ThrowIfNull(openAsset);
        cancellationToken.ThrowIfCancellationRequested();
        ElevationTileCatalog catalog = ElevationTileCatalog.Parse(indexJson);
        Dictionary<string, string> checksums = new(StringComparer.Ordinal);
        using IncrementalHash generationHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        generationHash.AppendData(Encoding.UTF8.GetBytes(indexJson));
        foreach (ElevationTileCatalog.Tile tile in catalog.Tiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string checksum = await ReadChecksumAsync(openAsset, tile.Directory + "/elevation.sqlite.sha256", cancellationToken)
                .ConfigureAwait(false);
            if (checksum.Length != 64 || !checksum.All(Uri.IsHexDigit))
            {
                throw ElevationTileCatalog.Invalid("elevation.sqlite.sha256", checksum, "64 hexadecimal SHA-256 digits");
            }
            checksums.Add(tile.Directory, checksum);
            generationHash.AppendData(Encoding.UTF8.GetBytes(tile.Directory + ":" + checksum + "\n"));
        }
        string generation = Convert.ToHexString(generationHash.GetHashAndReset());
        string elevation = Path.Combine(dataDirectory, "elevation");
        string generationDirectory = Path.Combine(elevation, ".bundled", generation);
        Directory.CreateDirectory(elevation);
        string staging = Path.Combine(elevation, ".staging-" + Guid.NewGuid().ToString("N"));
        string temporaryIndex = Path.Combine(elevation, "index-" + Guid.NewGuid().ToString("N") + ".tmp");
        string temporaryOwnership = temporaryIndex + ".sha256";
        try
        {
            bool existingGeneration = Directory.Exists(generationDirectory);
            string destination = existingGeneration ? generationDirectory : staging;
            foreach (ElevationTileCatalog.Tile tile in catalog.Tiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string directory = Path.Combine(destination, tile.Directory);
                string database = Path.Combine(directory, "elevation.sqlite");
                string identity = database + ".sha256";
                // Do not copy/hash the entire country on every startup. Every route still
                // hashes its opened shard snapshot through SqliteElevationProvider.
                if (existingGeneration && File.Exists(database) && File.Exists(identity)
                    && string.Equals((await File.ReadAllTextAsync(identity, cancellationToken).ConfigureAwait(false)).Trim(),
                        checksums[tile.Directory], StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                await ElevationPackageInstaller.InstallAsync(directory, checksums[tile.Directory],
                    () => OpenAsset(openAsset, tile.Directory + "/elevation.sqlite"), cancellationToken).ConfigureAwait(false);
                SqliteElevationProvider provider = new(directory);
                catalog.RequireSource(provider.Source, tile.Directory);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!existingGeneration)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(generationDirectory)!);
                Directory.Move(staging, generationDirectory);
            }
            JsonObject installed = JsonNode.Parse(indexJson)!.AsObject();
            JsonArray tiles = installed["tiles"]!.AsArray();
            for (int index = 0; index < catalog.Tiles.Count; index++)
            {
                tiles[index]!["directory"] = ".bundled/" + generation + "/" + catalog.Tiles[index].Directory;
            }
            string publishedJson = installed.ToJsonString();
            await File.WriteAllTextAsync(temporaryIndex, publishedJson, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(temporaryOwnership, Hash(publishedJson), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryOwnership, Path.Combine(elevation, OwnershipFileName), overwrite: true);
            File.Move(temporaryIndex, Path.Combine(elevation, "index.json"), overwrite: true);
        }
        finally
        {
            File.Delete(temporaryIndex);
            File.Delete(temporaryOwnership);
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    public static void RemoveInstalledPackage(string dataDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("A data directory is required.", nameof(dataDirectory));
        }
        cancellationToken.ThrowIfCancellationRequested();
        string elevation = Path.Combine(dataDirectory, "elevation");
        string ownership = Path.Combine(elevation, OwnershipFileName);
        string index = Path.Combine(elevation, "index.json");
        if (!File.Exists(ownership))
        {
            return;
        }
        if (File.Exists(index) && string.Equals(Hash(File.ReadAllText(index)), File.ReadAllText(ownership).Trim(),
            StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(index);
        }
        File.Delete(ownership);
    }

    private static async Task<string> ReadChecksumAsync(
        Func<string, Stream> openAsset, string path, CancellationToken cancellationToken)
    {
        using StreamReader reader = new(OpenAsset(openAsset, path));
        StringBuilder text = new();
        char[] buffer = new char[128];
        int length;
        while ((length = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            text.Append(buffer, 0, length);
            if (text.Length > 128)
            {
                throw ElevationTileCatalog.Invalid("elevation.sqlite.sha256", path, "a SHA-256 checksum text file");
            }
        }
        return text.ToString().Trim().ToUpperInvariant();
    }

    private static Stream OpenAsset(Func<string, Stream> openAsset, string path)
    {
        try
        {
            return openAsset(path);
        }
        catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw ElevationTileCatalog.Invalid("tiles.asset", path, "an existing bundled asset", error);
        }
    }

    private static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
}
