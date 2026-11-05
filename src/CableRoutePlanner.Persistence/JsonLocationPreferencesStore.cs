using System.Text.Json;
using CableRoutePlanner.Application;

namespace CableRoutePlanner.Persistence;

public sealed class JsonLocationPreferencesStore(string path) : ILocationPreferencesStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public LocationPreferences Load()
    {
        if (!File.Exists(path))
        {
            return new();
        }

        try
        {
            return JsonSerializer.Deserialize<LocationPreferences>(
                File.ReadAllText(path), SerializerOptions) ?? new();
        }
        catch (JsonException ex)
        {
            throw new PlanningException("Файл настроек местоположения повреждён.", ex);
        }
    }

    public void Save(LocationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        SaveAtomically(JsonSerializer.Serialize(preferences, SerializerOptions));
    }

    private void SaveAtomically(string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, contents);
        File.Move(temporaryPath, path, true);
    }
}
