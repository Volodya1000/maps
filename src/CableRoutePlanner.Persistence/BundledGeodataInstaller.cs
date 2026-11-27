using System.IO.Compression;

namespace CableRoutePlanner.Persistence;

public static class BundledGeodataInstaller
{
    public static async Task InstallAsync(string directory, string manifest,
        IReadOnlyList<string> assetNames, Func<string, Stream> openAsset,
        IProgress<string> progress, CancellationToken cancellationToken)
    {
        string localManifest = Path.Combine(directory, "manifest.json");
        string database = Path.Combine(directory, "geodata.sqlite");
        if (File.Exists(database)
            && File.Exists(localManifest)
            && await File.ReadAllTextAsync(localManifest, cancellationToken).ConfigureAwait(false) == manifest)
        {
            return;
        }

        await Task.Run(async () =>
        {
            progress.Report("Распаковка карты. Первый запуск может занять некоторое время…");
            string[] chunks = assetNames.Where(name => name.StartsWith("geodata.", StringComparison.Ordinal)
                    && name.EndsWith(".gz", StringComparison.Ordinal)
                    && name != "geodata.sqlite.gz")
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();
            string[] files = chunks.Length > 0 ? chunks
                : new[] { assetNames.Contains("geodata.sqlite.gz", StringComparer.Ordinal)
                    ? "geodata.sqlite.gz"
                    : "geodata.sqlite" };
            try
            {
                await using FileStream output = File.Create(database + ".tmp");
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

                await output.DisposeAsync().ConfigureAwait(false);
                File.Move(database + ".tmp", database, true);
                await File.WriteAllTextAsync(localManifest, manifest, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                File.Delete(database + ".tmp");
            }
        }, cancellationToken).ConfigureAwait(false);
    }
}
