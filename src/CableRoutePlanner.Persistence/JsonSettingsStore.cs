using System.Text.Json;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;

namespace CableRoutePlanner.Persistence;

public sealed class JsonSettingsStore(string path) : ISettingsStore
{
    public RoutingOptions Load()
    {
        if (!File.Exists(path))
        {
            return NormalizeMandatoryCrossingPreferences(new());
        }

        try
        {
            RoutingOptions options = JsonSerializer.Deserialize<RoutingOptions>(File.ReadAllText(path))
                ?? throw new JsonException("Настройки имеют значение null.");
            options.Validate();
            return NormalizeMandatoryCrossingPreferences(options);
        }
        catch (JsonException ex)
        {
            throw new PlanningException("Файл настроек повреждён и не может быть прочитан.", ex);
        }
        catch (ArgumentException ex)
        {
            throw new PlanningException("Файл настроек содержит недопустимые значения.", ex);
        }
    }

    public void Save(RoutingOptions options)
    {
        options = NormalizeMandatoryCrossingPreferences(options);
        options.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(options));
        File.Move(temp, path, true);
    }

    private static RoutingOptions NormalizeMandatoryCrossingPreferences(RoutingOptions options) => options with
    {
        // Keep legacy JSON readable, but user settings cannot disable mapped
        // crossing support. Verify and tests pass RoutingOptions directly.
        PreferOverheadGasCrossings = true,
        AllowExperimentalPedestrianUnderpasses = true
    };
}
