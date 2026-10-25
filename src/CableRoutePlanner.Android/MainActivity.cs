using System.IO.Compression;
using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;
using Avalonia.ReactiveUI;
using CableRoutePlanner.Composition;
using CableRoutePlanner.UI;

namespace CableRoutePlanner.Android;

[Activity(
    Label = "Кабельные маршруты",
    Theme = "@style/CableRoutePlannerTheme",
    MainLauncher = true,
    Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity<PlannerApplication>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        var directory = Path.Combine(FilesDir!.AbsolutePath, "Data");
        Directory.CreateDirectory(directory);
        return builder.AfterSetup(b =>
        {
            AvaloniaAppBootstrap.ConfigureApplication(
                (PlannerApplication)b.Instance!,
                directory,
                Path.Combine(FilesDir.AbsolutePath, "settings.json"),
                (progress, ct) => PrepareDataAsync(directory, progress, ct));
        }).UseReactiveUI();
    }

    private async Task PrepareDataAsync(string directory, IProgress<string> progress, CancellationToken ct)
    {
        var sourceManifest = Assets!.Open("Data/manifest.json");
        using var reader = new StreamReader(sourceManifest);
        var manifest = await reader.ReadToEndAsync(ct);
        var localManifest = Path.Combine(directory, "manifest.json");
        var database = Path.Combine(directory, "geodata.sqlite");
        if (File.Exists(database)
            && File.Exists(localManifest)
            && await File.ReadAllTextAsync(localManifest, ct) == manifest)
        {
            return;
        }

        await Task.Run(async () =>
        {
            progress.Report("Распаковка карты. Первый запуск может занять некоторое время…");
            var names = Assets.List("Data")!;
            var chunks = names.Where(name => name.StartsWith("geodata.", StringComparison.Ordinal)
                    && name.EndsWith(".gz", StringComparison.Ordinal)
                    && name != "geodata.sqlite.gz")
                .Order(StringComparer.Ordinal).ToArray();
            var files = chunks.Length > 0 ? chunks
                : new[] { names.Contains("geodata.sqlite.gz", StringComparer.Ordinal)
                    ? "geodata.sqlite.gz"
                    : "geodata.sqlite" };
            await using var output = File.Create(database + ".tmp");
            var buffer = new byte[1024 * 1024];
            long copied = 0;
            foreach (var file in files)
            {
                using var asset = Assets.Open("Data/" + file);
                using Stream input = file.EndsWith(".gz", StringComparison.Ordinal)
                    ? new GZipStream(asset, CompressionMode.Decompress) : asset;
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    copied += count;
                    if (copied % (16 * 1024 * 1024) < buffer.Length)
                    {
                        progress.Report($"Подготовка карты: {copied / (1024 * 1024)} МБ…");
                    }
                }
            }

            await output.DisposeAsync();
            File.Move(database + ".tmp", database, true);
            await File.WriteAllTextAsync(localManifest, manifest, ct);
        }, ct);
    }
}
