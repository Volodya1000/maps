using System.Diagnostics;
using System.Text.Json;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;

namespace CableRoutePlanner.Verify;

internal static class PackageInspection
{
    public static async Task RunAsync(IGeodataRepository repository, string output)
    {
        // Administrative-centre coordinates are probes, never routing inputs or stored routes.
        var cities = new (string Name, GeoCoordinate Coordinate)[]
        {
            ("Минск", new(53.9023, 27.5619)),
            ("Брест", new(52.0976, 23.7341)),
            ("Гродно", new(53.6694, 23.8131)),
            ("Витебск", new(55.1904, 30.2049)),
            ("Гомель", new(52.4345, 30.9754)),
            ("Могилёв", new(53.9006, 30.3317))
        };
        var probes = new List<object>();
        var timer = Stopwatch.StartNew();
        var overview = await repository.QueryMapAsync(repository.Info.Bounds, 1500, default);
        if (overview.Count == 0 || !overview.Any(feature => feature.Kind == TerrainKind.Road))
        {
            throw new PlanningException("Country overview has no roads.");
        }

        probes.Add(new { area = "country-overview", count = overview.Count, elapsed_ms = timer.ElapsedMilliseconds });
        foreach (var (name, coordinate) in cities)
        {
            timer.Restart();
            var area = GeoBounds.Around(new[] { coordinate }, 0.004);
            var features = await repository.QueryAsync(area, default);
            if (features.Count == 0 || !features.Any(feature => feature.Kind == TerrainKind.Road))
            {
                throw new PlanningException($"No local road data near {name}.");
            }

            probes.Add(new
            {
                area = name,
                count = features.Count,
                elapsed_ms = timer.ElapsedMilliseconds,
                kinds = features.GroupBy(feature => feature.Kind).ToDictionary(group => group.Key.ToString(), group => group.Count())
            });
        }

        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            repository.Info.RegionId,
            repository.Info.Bounds,
            probes
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Verified country overview and {cities.Length} regional centres: {output}");
    }
}
