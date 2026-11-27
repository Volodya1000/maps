using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CableRoutePlanner.Persistence;

public static class BundledGeodataInstaller
{
    public static async Task InstallAsync(string directory, string manifest,
        IReadOnlyList<string> assetNames, Func<string, Stream> openAsset,
        IProgress<string> progress, CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonDocument.Parse(manifest);
        string? expectedHash = document.RootElement.GetProperty("database_sha256").GetString();
        if (expectedHash is null || expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("manifest.database_sha256 must contain 64 hexadecimal digits.");
        }
        string localManifest = Path.Combine(directory, "manifest.json");
        string database = Path.Combine(directory, "geodata.sqlite");
        await using FileStream installationLock = await AcquireLockAsync(directory, cancellationToken).ConfigureAwait(false);
        await Task.Run(async () =>
        {
            if (File.Exists(database) && File.Exists(localManifest)
                && await File.ReadAllTextAsync(localManifest, cancellationToken).ConfigureAwait(false) == manifest
                && await IsValidDatabaseAsync(database, expectedHash, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            progress.Report("Распаковка карты. Первый запуск может занять некоторое время…");
            string workspace = Path.Combine(directory, ".geodata-install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            string candidate = Path.Combine(workspace, "geodata.sqlite");
            string candidateManifest = Path.Combine(workspace, "manifest.json");
            string backup = Path.Combine(workspace, "previous.sqlite");
            bool preserveBackup = false;
            try
            {
                await ExtractAsync(candidate, assetNames, openAsset, progress, cancellationToken).ConfigureAwait(false);
                await ValidateDatabaseAsync(candidate, expectedHash, cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(candidateManifest, manifest, cancellationToken).ConfigureAwait(false);
                progress.Report("Публикация карты…");
                cancellationToken.ThrowIfCancellationRequested();

                // No cancellable work after this boundary: either publish the pair or restore
                // the previous database. Keep its backup until manifest publication succeeds.
                bool hadDatabase = File.Exists(database);
                if (hadDatabase)
                {
                    File.Replace(candidate, database, backup);
                }
                else
                {
                    File.Move(candidate, database);
                }
                try
                {
                    File.Move(candidateManifest, localManifest, overwrite: true);
                }
                catch (Exception publicationError) when (publicationError is IOException or UnauthorizedAccessException)
                {
                    try
                    {
                        if (hadDatabase)
                        {
                            File.Move(backup, database, overwrite: true);
                        }
                        else
                        {
                            File.Delete(database);
                        }
                    }
                    catch (Exception recoveryError) when (recoveryError is IOException or UnauthorizedAccessException)
                    {
                        // Do not delete the only recoverable copy if the filesystem also
                        // refuses rollback. Both errors and the backup path remain available.
                        preserveBackup = true;
                        throw new AggregateException($"Map rollback failed; recovery files: {workspace}",
                            publicationError, recoveryError);
                    }
                    throw;
                }
            }
            finally
            {
                if (!preserveBackup)
                {
                    Directory.Delete(workspace, recursive: true);
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FileStream> AcquireLockAsync(string directory, CancellationToken cancellationToken)
    {
        string path = Path.Combine(directory, ".geodata-install.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // The lock file is deliberately persistent: deleting it would let a new
                // process lock a different inode while another installer still owns this one.
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when ((exception.HResult & 0xFFFF) is 11 or 32 or 33)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task ExtractAsync(string candidate, IReadOnlyList<string> assetNames,
        Func<string, Stream> openAsset, IProgress<string> progress, CancellationToken cancellationToken)
    {
        string[] chunks = assetNames.Where(name => name.StartsWith("geodata.", StringComparison.Ordinal)
                && name.EndsWith(".gz", StringComparison.Ordinal) && name != "geodata.sqlite.gz")
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        string[] files = chunks.Length > 0 ? chunks
            : [assetNames.Contains("geodata.sqlite.gz", StringComparer.Ordinal) ? "geodata.sqlite.gz" : "geodata.sqlite"];
        await using FileStream output = File.Create(candidate);
        byte[] buffer = new byte[1024 * 1024];
        long copied = 0;
        foreach (string file in files)
        {
            using Stream asset = openAsset(file);
            using Stream input = file.EndsWith(".gz", StringComparison.Ordinal)
                ? new GZipStream(asset, CompressionMode.Decompress) : asset;
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                copied += count;
                if (copied % (16 * 1024 * 1024) < buffer.Length)
                {
                    progress.Report($"Подготовка карты: {copied / (1024 * 1024)} МБ…");
                }
            }
        }
    }

    private static async Task<bool> IsValidDatabaseAsync(string database, string expectedHash,
        CancellationToken cancellationToken)
    {
        try
        {
            await ValidateDatabaseAsync(database, expectedHash, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static async Task ValidateDatabaseAsync(string database, string expectedHash,
        CancellationToken cancellationToken)
    {
        await using (FileStream stream = File.OpenRead(database))
        {
            using SHA256 hash = SHA256.Create();
            string actualHash = Convert.ToHexString(await hash.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"database_sha256 mismatch: expected {expectedHash}, actual {actualHash}.");
            }
        }
        try
        {
            using SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check";
            using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || reader.GetString(0) != "ok"
                || await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("The unpacked SQLite database failed integrity_check.");
            }
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("The unpacked SQLite database is invalid.", exception);
        }
    }
}
