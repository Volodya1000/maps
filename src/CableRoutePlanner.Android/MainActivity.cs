using System.IO.Compression;
using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;
using Avalonia.ReactiveUI;
using CableRoutePlanner.Application;
using CableRoutePlanner.Infrastructure;
using CableRoutePlanner.Persistence;
using CableRoutePlanner.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        string directory = Path.Combine(FilesDir!.AbsolutePath, "Data");
        Directory.CreateDirectory(directory);
        return builder.AfterSetup(b =>
        {
            ServiceCollection services = new();
            services.AddLogging(logging => logging.AddConsole());
            services.AddInfrastructure();
            services.AddPersistence(directory, Path.Combine(FilesDir!.AbsolutePath, "settings.json"));
            services.AddSingleton<IPlanningSession, PlanningSession>();
            services.AddPresentation((progress, ct) => PrepareDataAsync(directory, progress, ct));
            IServiceProvider provider = services.BuildServiceProvider(
                new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            PresentationServiceCollectionExtensions.ConfigureApplication(
                (PlannerApplication)b.Instance!, provider, directory);
        }).UseReactiveUI();
    }

    private async Task PrepareDataAsync(string directory, IProgress<string> progress, CancellationToken ct)
    {
        Stream sourceManifest = Assets!.Open("Data/manifest.json");
        using StreamReader reader = new(sourceManifest);
        string manifest = await reader.ReadToEndAsync(ct);
        string localManifest = Path.Combine(directory, "manifest.json");
        string database = Path.Combine(directory, "geodata.sqlite");
        if (File.Exists(database)
            && File.Exists(localManifest)
            && await File.ReadAllTextAsync(localManifest, ct) == manifest)
        {
            return;
        }

        await Task.Run(async () =>
        {
            progress.Report("Распаковка карты. Первый запуск может занять некоторое время…");
            string[] names = Assets.List("Data")!;
            string[] chunks = names.Where(name => name.StartsWith("geodata.", StringComparison.Ordinal)
                    && name.EndsWith(".gz", StringComparison.Ordinal)
                    && name != "geodata.sqlite.gz")
                .Order(StringComparer.Ordinal).ToArray();
            string[] files = chunks.Length > 0 ? chunks
                : new[] { names.Contains("geodata.sqlite.gz", StringComparer.Ordinal)
                    ? "geodata.sqlite.gz"
                    : "geodata.sqlite" };
            await using FileStream output = File.Create(database + ".tmp");
            byte[] buffer = new byte[1024 * 1024];
            long copied = 0;
            foreach (string file in files)
            {
                using Stream asset = Assets.Open("Data/" + file);
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
