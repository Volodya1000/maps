using System.Text.Json;
using CableRoutePlanner.Domain;
using Xunit;

namespace CableRoutePlanner.Tests;

internal static class RoutingGolden
{
    public static void Compare(JsonElement expected, JsonElement actual, string path)
    {
        Assert.Equal(expected.ValueKind, actual.ValueKind);
        switch (expected.ValueKind)
        {
            case JsonValueKind.Array:
                Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength());
                for (int index = 0; index < expected.GetArrayLength(); index++)
                { Compare(expected[index], actual[index], $"{path}[{index}]"); }
                break;
            case JsonValueKind.Object:
                Assert.Equal(expected.EnumerateObject().Select(property => property.Name),
                    actual.EnumerateObject().Select(property => property.Name));
                foreach (JsonProperty property in expected.EnumerateObject())
                { Compare(property.Value, actual.GetProperty(property.Name), path + "." + property.Name); }
                break;
            case JsonValueKind.Number:
                if (expected.TryGetInt64(out long expectedInteger) && actual.TryGetInt64(out long actualInteger))
                { Assert.Equal(expectedInteger, actualInteger); }
                else
                {
                    double tolerance = path.EndsWith(".Latitude", StringComparison.Ordinal)
                        || path.EndsWith(".Longitude", StringComparison.Ordinal) ? 1e-9 : 1e-6;
                    Assert.True(Math.Abs(expected.GetDouble() - actual.GetDouble()) <= tolerance,
                        $"{path}: expected {expected}, actual {actual}, tolerance {tolerance}");
                }
                break;
            default:
                Assert.Equal(expected.GetRawText(), actual.GetRawText());
                break;
        }
    }
}

// The archived routing fixtures capture the ten metrics that existed at recording time.
internal sealed class RoutingMetricsSnapshot(RouteMetrics metrics)
{
    public double LengthMetres { get; } = metrics.LengthMetres;
    public double ForestMetres { get; } = metrics.ForestMetres;
    public double FieldMetres { get; } = metrics.FieldMetres;
    public int RoadCrossings { get; } = metrics.RoadCrossings;
    public int ApprovedWaterCrossings { get; } = metrics.ApprovedWaterCrossings;
    public int PotentialBridgeCrossings { get; } = metrics.PotentialBridgeCrossings;
    public double WeightedCost { get; } = metrics.WeightedCost;
    public double? AscentMetres { get; } = metrics.AscentMetres;
    public int PotentialGasPipelineCrossings { get; } = metrics.PotentialGasPipelineCrossings;
    public int RailwayCrossings { get; } = metrics.RailwayCrossings;
}
