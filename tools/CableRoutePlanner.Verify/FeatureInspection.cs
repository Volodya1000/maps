using System.Text.Json;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;

namespace CableRoutePlanner.Verify;

internal static class FeatureInspection
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static async Task RunAsync(IGeodataRepository repository, string directory, string output)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        IReadOnlyList<MapFeature> features = await repository.QueryAsync(repository.Info.Bounds, default).ConfigureAwait(false);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            attribution = repository.Info.Attribution,
            schemaVersion = manifest.RootElement.GetProperty("schema_version").GetInt32(),
            terrainKinds = Enum.GetNames<TerrainKind>(),
            features = features.Select(feature => new
            {
                id = feature.Id,
                kind = feature.Kind.ToString(),
                name = feature.Name,
                approvalId = feature.ApprovalId,
                wkbBytes = feature.Wkb.Length,
                underpassRoadIds = feature.UnderpassRoadIds,
                underpassTopology = feature.UnderpassTopology is { } topology ? new
                {
                    networkId = topology.NetworkId,
                    nodeIds = topology.NodeIds,
                    entranceNodeIds = topology.EntranceNodeIds
                } : null
            })
        }, JsonOptions)).ConfigureAwait(false);
    }
}
