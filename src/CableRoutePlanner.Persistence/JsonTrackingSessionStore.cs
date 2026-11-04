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
            RouteProgressSnapshot? snapshot = JsonSerializer.Deserialize<RouteProgressSnapshot>(
                File.ReadAllText(path), SerializerOptions);
            if (snapshot is null)
            {
                throw new PlanningException("Файл сессии обхода пуст или имеет неверный формат.");
            }

            Validate(snapshot);
            return snapshot;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException
            or FormatException)
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

    private static void Validate(RouteProgressSnapshot snapshot)
    {
        if (snapshot.SessionId == Guid.Empty
            || string.IsNullOrWhiteSpace(snapshot.RouteFingerprint)
            || snapshot.Geometry is null
            || snapshot.Geometry.Count < 2
            || snapshot.CoveredIntervals is null
            || !Enum.IsDefined(snapshot.Status)
            || snapshot.UpdatedUtc < snapshot.StartedUtc
            || snapshot.Geometry.Any(point => !double.IsFinite(point.Latitude)
                || !double.IsFinite(point.Longitude)))
        {
            throw new InvalidOperationException("Недопустимая структура сессии обхода.");
        }

        double total = snapshot.Geometry.Zip(snapshot.Geometry.Skip(1), (first, second) => first.DistanceTo(second)).Sum();
        if (!double.IsFinite(total) || total <= 0
            || snapshot.CoveredIntervals.Any(interval =>
                !double.IsFinite(interval.StartMetres)
                || !double.IsFinite(interval.EndMetres)
                || interval.StartMetres < 0
                || interval.EndMetres <= interval.StartMetres
                || interval.EndMetres > total))
        {
            throw new InvalidOperationException("Недопустимые интервалы сессии обхода.");
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
