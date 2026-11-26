using System.Text.Json;

namespace CableRoutePlanner.Persistence;

// Positional record constructors otherwise supply zero/default for missing JSON
// members. Version 1 writes every calculation field, including nullable metadata.
internal static class SavedRouteJsonSchema
{
    private static readonly string[] FileFields = ["Version", "Routes"];
    private static readonly string[] SnapshotFields = ["Id", "AddedUtc", "Points", "Alternatives", "SelectedAlternativeId", "Options"];
    private static readonly string[] PointFields = ["Id", "Coordinate"];
    private static readonly string[] OptionFields = ["CellSizeMetres", "MaxNodes", "ReserveFraction", "PreferOverheadGasCrossings", "MaxRouteLengthMetres", "AllowExperimentalPedestrianUnderpasses"];
    private static readonly string[] AlternativeFields = ["Id", "Name", "Geometry", "Metrics", "TransitionPoints", "RoadContacts", "GasPipelineCandidateIds", "UnderpassCandidateIds", "RoadCrossingEvents", "UnderpassPassages"];
    private static readonly string[] MetricFields = ["LengthMetres", "ForestMetres", "FieldMetres", "RoadCrossings", "ApprovedWaterCrossings", "PotentialBridgeCrossings", "WeightedCost", "AscentMetres", "PotentialGasPipelineCrossings", "RailwayCrossings"];
    private static readonly string[] ContactFields = ["RoadId", "Kind", "Geometry"];
    private static readonly string[] CrossingFields = ["RoadId", "Kind", "Location"];
    private static readonly string[] PassageFields = ["NetworkId", "Entry", "Exit"];

    public static void Validate(JsonElement file)
    {
        Require(file, "SavedRoutesFile", FileFields);
        foreach (JsonElement snapshot in file.GetProperty("Routes").EnumerateArray())
        {
            Require(snapshot, "SavedRoute", SnapshotFields);
            Require(snapshot.GetProperty("Options"), "SavedRoute.Options", OptionFields);
            foreach (JsonElement point in snapshot.GetProperty("Points").EnumerateArray())
            {
                Require(point, "SavedRoute.Points[]", PointFields);
            }
            foreach (JsonElement alternative in snapshot.GetProperty("Alternatives").EnumerateArray())
            {
                Require(alternative, "RouteAlternative", AlternativeFields);
                Require(alternative.GetProperty("Metrics"), "RouteAlternative.Metrics", MetricFields);
                ValidateOptionalArray(alternative, "RoadContacts", ContactFields);
                ValidateOptionalArray(alternative, "RoadCrossingEvents", CrossingFields);
                ValidateOptionalArray(alternative, "UnderpassPassages", PassageFields);
            }
        }
    }

    private static void ValidateOptionalArray(JsonElement alternative, string name, string[] fields)
    {
        JsonElement array = alternative.GetProperty(name);
        if (array.ValueKind == JsonValueKind.Null)
        {
            return;
        }
        foreach (JsonElement item in array.EnumerateArray())
        {
            Require(item, "RouteAlternative." + name + "[]", fields);
        }
    }

    private static void Require(JsonElement value, string path, string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"{path}={value.ValueKind}: требуется объект.");
        }
        foreach (string field in fields)
        {
            if (!value.TryGetProperty(field, out _))
            {
                throw new JsonException($"{path}.{field}: обязательное поле отсутствует.");
            }
        }
    }
}
