using System.Text.Json;
using System.Text.Json.Serialization;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;

namespace CableRoutePlanner.Persistence;

public sealed class JsonTrackingSessionStore(string path) : ITrackingSessionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    static JsonTrackingSessionStore()
    {
        SerializerOptions.Converters.Add(new GeoCoordinateConverter());
    }

    public RouteProgressSnapshot? Load()
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RouteProgressSnapshot>(
                File.ReadAllText(path), SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new PlanningException("Файл сессии обхода повреждён.", ex);
        }
    }

    public void Save(RouteProgressSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot, SerializerOptions));
        File.Move(temporaryPath, path, true);
    }

    public void Clear()
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed class GeoCoordinateConverter : JsonConverter<GeoCoordinate>
    {
        public override GeoCoordinate Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            JsonElement root = document.RootElement;
            double latitude = root.GetProperty("Latitude").GetDouble();
            double longitude = root.GetProperty("Longitude").GetDouble();
            return new GeoCoordinate(latitude, longitude);
        }

        public override void Write(
            Utf8JsonWriter writer,
            GeoCoordinate value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("Latitude", value.Latitude);
            writer.WriteNumber("Longitude", value.Longitude);
            writer.WriteEndObject();
        }
    }
}
