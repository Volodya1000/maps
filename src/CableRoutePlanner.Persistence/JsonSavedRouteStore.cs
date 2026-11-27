using System.Text.Json;
using CableRoutePlanner.Application;

namespace CableRoutePlanner.Persistence;

public sealed class JsonSavedRouteStore(string path) : ISavedRouteStore
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly object _gate = new();

    public IReadOnlyList<SavedRoute> List()
    {
        lock (_gate)
        {
            return Read().OrderByDescending(route => route.AddedUtc).ToArray();
        }
    }

    public SavedRoute Read(Guid id)
    {
        lock (_gate)
        {
            return Read().FirstOrDefault(route => route.Id == id)
                ?? throw new PlanningException("Сохранённый маршрут не найден. Обновите список.");
        }
    }

    public void Add(SavedRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        route.Validate();
        lock (_gate)
        {
            List<SavedRoute> routes = Read();
            if (routes.Any(existing => existing.Id == route.Id))
            {
                throw new ArgumentException($"SavedRoute.Id={route.Id}: запись с таким ID уже существует.", nameof(route));
            }
            routes.Add(route);
            Write(routes);
        }
    }

    public void Delete(Guid id)
    {
        lock (_gate)
        {
            List<SavedRoute> routes = Read();
            if (routes.RemoveAll(route => route.Id == id) > 0)
            {
                Write(routes);
            }
        }
    }

    private List<SavedRoute> Read()
    {
        if (!File.Exists(path))
        {
            return [];
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            SavedRouteJsonSchema.Validate(document.RootElement);
            List<SavedRoute> routes = [];
            foreach (JsonElement snapshot in document.RootElement.GetProperty("Routes").EnumerateArray())
            {
                SavedRoute route = snapshot.Deserialize<SavedRoute>(SerializerOptions)
                    ?? throw new JsonException("SavedRoute=null");
                route.Validate();
                routes.Add(SavedRouteElevation.Restore(route, snapshot.GetProperty("Alternatives"), SerializerOptions));
            }
            if (routes.Select(route => route.Id).Distinct().Count() != routes.Count)
            {
                throw new JsonException("SavedRoutesFile.Routes: ID записей должны быть уникальны.");
            }
            return routes;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException
            or KeyNotFoundException or FormatException)
        {
            throw new PlanningException("Не удалось прочитать сохранённые маршруты: файл повреждён или имеет неподдерживаемый формат.", ex);
        }
    }

    private void Write(List<SavedRoute> routes)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new SavedRoutesFile(SavedRouteJsonSchema.CurrentVersion, routes), SerializerOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = new();
        options.Converters.Add(new GeoCoordinateJsonConverter());
        return options;
    }

    private sealed record SavedRoutesFile(int Version, List<SavedRoute> Routes);
}
