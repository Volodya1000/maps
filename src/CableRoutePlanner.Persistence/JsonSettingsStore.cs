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
            return new();
        }

        try
        {
            RoutingOptions options = JsonSerializer.Deserialize<RoutingOptions>(File.ReadAllText(path))
                ?? throw new JsonException("Настройки имеют значение null.");
            options.Validate();
            // The gas-crossing preference is now always enabled by the application.
            // Keep reading the legacy field so existing settings files remain valid,
            // but normalize an old `false` value before it reaches routing.
            return options with { PreferOverheadGasCrossings = true };
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
        options = options with { PreferOverheadGasCrossings = true };
        options.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(options));
        File.Move(temp, path, true);
    }
}
