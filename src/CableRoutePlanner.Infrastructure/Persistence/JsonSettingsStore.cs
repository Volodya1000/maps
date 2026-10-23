using System.Text.Json;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
namespace CableRoutePlanner.Infrastructure.Persistence;
public sealed class JsonSettingsStore(string path) : ISettingsStore
{
    public RoutingOptions Load()
    {
        if (!File.Exists(path)) return new();
        try { var options = JsonSerializer.Deserialize<RoutingOptions>(File.ReadAllText(path)) ?? new(); options.Validate(); return options; }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return new(); }
    }
    public void Save(RoutingOptions options)
    {
        options.Validate(); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(options)); File.Move(temp, path, true);
    }
}
