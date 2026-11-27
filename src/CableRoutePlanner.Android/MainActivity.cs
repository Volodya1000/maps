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
    private AndroidLocationService? _locationService;
    private AndroidHeadingService? _headingService;

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        string directory = Path.Combine(FilesDir!.AbsolutePath, "Data");
        Directory.CreateDirectory(directory);
        return builder.AfterSetup(b =>
        {
            ServiceCollection services = new();
            services.AddLogging(logging => logging.AddConsole());
            services.AddInfrastructure();
            services.AddPersistence(directory, Path.Combine(FilesDir!.AbsolutePath, "settings.json"), CacheDir!.AbsolutePath);
            _locationService = new AndroidLocationService(this);
            _headingService = new AndroidHeadingService(this);
            services.AddSingleton<ILocationService>(_locationService);
            services.AddSingleton<IHeadingService>(_headingService);
            services.AddSingleton<IPlanningSession, PlanningSession>();
            services.AddPresentation((progress, ct) => PrepareDataAsync(directory, progress, ct));
            IServiceProvider provider = services.BuildServiceProvider(
                new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            PresentationServiceCollectionExtensions.ConfigureApplication(
                (PlannerApplication)b.Instance!, provider, directory);
        }).UseReactiveUI();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _locationService?.ResumeUpdates();
    }

    protected override void OnPause()
    {
        _locationService?.PauseUpdates();
        _headingService?.PauseUpdates();
        base.OnPause();
    }

    public override void OnRequestPermissionsResult(
        int requestCode,
        string[] permissions,
        Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == 4101)
        {
            _locationService?.OnRequestPermissionsResult();
        }
    }

    private async Task PrepareElevationAsync(string directory, IProgress<string> progress, CancellationToken ct)
    {
        string[] names = Assets!.List("Data")!;
        if (names.Contains("elevation", StringComparer.Ordinal)
            && Assets.List("Data/elevation")!.Contains("index.json", StringComparer.Ordinal))
        {
            using StreamReader catalogReader = new(Assets.Open("Data/elevation/index.json"));
            string catalog = await catalogReader.ReadToEndAsync(ct);
            progress.Report("Подготовка офлайн-высот Беларуси…");
            await TiledElevationPackageInstaller.InstallAsync(directory, catalog,
                path => Assets.Open("Data/elevation/" + path), ct);
            return;
        }

        TiledElevationPackageInstaller.RemoveInstalledPackage(directory, ct);
        bool hasDatabase = names.Contains("elevation.sqlite", StringComparer.Ordinal);
        bool hasIdentity = names.Contains("elevation.sqlite.sha256", StringComparer.Ordinal);
        if (hasDatabase != hasIdentity)
        {
            throw new InvalidDataException("Elevation assets require both database and SHA-256 identity.");
        }
        if (!hasDatabase)
        {
            ElevationPackageInstaller.RemoveInstalledPackage(directory, ct);
            return;
        }

        using StreamReader identityReader = new(Assets.Open("Data/elevation.sqlite.sha256"));
        string identity = await identityReader.ReadToEndAsync(ct);
        progress.Report("Проверка офлайн-пакета высот…");
        await ElevationPackageInstaller.InstallAsync(directory, identity,
            () => Assets.Open("Data/elevation.sqlite"), ct);
    }
    private async Task PrepareDataAsync(string directory, IProgress<string> progress, CancellationToken ct)
    {
        await PrepareElevationAsync(directory, progress, ct);
        Stream sourceManifest = Assets!.Open("Data/manifest.json");
        using StreamReader reader = new(sourceManifest);
        string manifest = await reader.ReadToEndAsync(ct);
        await BundledGeodataInstaller.InstallAsync(directory, manifest,
            Assets.List("Data")!, name => Assets.Open("Data/" + name), progress, ct);
    }
}
