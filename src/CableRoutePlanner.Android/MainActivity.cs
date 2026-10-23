using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;
using Avalonia.ReactiveUI;
using CableRoutePlanner.Bootstrap;
using CableRoutePlanner.UI;
namespace CableRoutePlanner.Android;
[Activity(Label = "Кабельные маршруты", MainLauncher = true, Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity<PlannerApplication>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        var directory = Path.Combine(FilesDir!.AbsolutePath, "Data");
        Directory.CreateDirectory(directory);
        foreach (var filename in new[] { "manifest.json", "geodata.sqlite" })
        {
            var destination = Path.Combine(directory, filename);
            using var input = Assets!.Open("Data/" + filename);
            using var output = File.Create(destination + ".tmp"); input.CopyTo(output); output.Close();
            File.Move(destination + ".tmp", destination, true);
        }
        return builder.AfterSetup(b =>
        {
            AppBootstrap.ConfigureApplication((PlannerApplication)b.Instance!, directory,
                Path.Combine(FilesDir.AbsolutePath, "settings.json"));
        }).UseReactiveUI();
    }
}
