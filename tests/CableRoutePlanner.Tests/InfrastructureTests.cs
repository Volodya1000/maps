using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Persistence;
using CableRoutePlanner.Infrastructure.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public async Task RealPackageContainsIndexedRoadsWaterForestAndMarsh()
    {
        var data = new SqliteGeodataRepository(TestData.RealData);
        var all = await data.QueryAsync(data.Info.Bounds, default);
        foreach (var kind in new[]
        {
            TerrainKind.Road,
            TerrainKind.River,
            TerrainKind.Forest,
            TerrainKind.Marsh,
            TerrainKind.BridgeCandidate
        }

        )
        {
            Assert.Contains(all, f => f.Kind == kind);
        }

        var subset = await data.QueryAsync(new(27.5, 53.94, 27.501, 53.941), default);
        Assert.NotEmpty(subset);
        Assert.True(subset.Count < all.Count);
        Assert.DoesNotContain(all, f => f.Kind == TerrainKind.ApprovedCrossing);
        Assert.False(data.Info.HasTerrain);
    }

    [Fact]
    public void MissingAndCorruptedPackageAreRejected()
    {
        using var fixture = new TemporaryDirectory();
        var directory = fixture.DirectoryPath;
        Assert.Throws<PlanningException>(() => new SqliteGeodataRepository(directory));
        File.Copy(Path.Combine(TestData.RealData, "manifest.json"), Path.Combine(directory, "manifest.json"));
        File.WriteAllText(Path.Combine(directory, "geodata.sqlite"), "corrupted");
        Assert.Throws<PlanningException>(() => new SqliteGeodataRepository(directory));
    }

    [Fact]
    public void SettingsSurviveRestartAndInvalidValuesDoNotOverwriteFile()
    {
        using var fixture = new TemporaryDirectory();
        var path = Path.Combine(fixture.DirectoryPath, "settings.json");
        var store = new JsonSettingsStore(path);
        store.Save(new(35, 50000, 0.1));
        Assert.Equal(35, new JsonSettingsStore(path).Load().CellSizeMetres);
        Assert.Throws<ArgumentException>(() => store.Save(new(-1)));
        Assert.Equal(35, store.Load().CellSizeMetres);
    }

    [Fact]
    public async Task RealOsmRouteIsComputedFromPackage()
    {
        var data = new SqliteGeodataRepository(TestData.RealData);
        var planner = new GridRoutePlanner(data, NullLogger<GridRoutePlanner>.Instance);
        // OSM service road in the real Novinki area; endpoints chosen on mapped free ground.
        var result = await planner.PlanAsync([new(53.9490, 27.5180), new(53.9500, 27.5190)], new(25), default);
        Assert.NotEmpty(result);
        Assert.All(result, r => Assert.InRange(r.Metrics.LengthMetres, 110, 2000));
    }
}
