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
            SavedRoutesFile file = document.RootElement.Deserialize<SavedRoutesFile>(SerializerOptions)
                ?? throw new JsonException("SavedRoutesFile=null");
            if (file.Version != 1 || file.Routes is null)
            {
                throw new JsonException($"SavedRoutesFile.Version={file.Version}: требуется версия 1 и список Routes.");
            }
            foreach (SavedRoute route in file.Routes)
            {
                ArgumentNullException.ThrowIfNull(route);
                route.Validate();
            }
            if (file.Routes.Select(route => route.Id).Distinct().Count() != file.Routes.Count)
            {
                throw new JsonException("SavedRoutesFile.Routes: ID записей должны быть уникальны.");
            }
            return file.Routes;
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
                JsonSerializer.Serialize(stream, new SavedRoutesFile(1, routes), SerializerOptions);
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
