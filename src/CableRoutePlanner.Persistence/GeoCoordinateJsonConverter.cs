using System.Text.Json;
using System.Text.Json.Serialization;
using CableRoutePlanner.Domain;

namespace CableRoutePlanner.Persistence;

internal sealed class GeoCoordinateJsonConverter : JsonConverter<GeoCoordinate>
{
    public override GeoCoordinate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;
        return new(root.GetProperty("Latitude").GetDouble(), root.GetProperty("Longitude").GetDouble());
    }

    public override void Write(Utf8JsonWriter writer, GeoCoordinate value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("Latitude", value.Latitude);
        writer.WriteNumber("Longitude", value.Longitude);
        writer.WriteEndObject();
    }
}
