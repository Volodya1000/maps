using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class PersistenceTests
{
    [Theory]
    [InlineData(500, 0, 501)]
    [InlineData(100, 1, 1501)]
    [InlineData(20, 2, 5001)]
    [InlineData(5, -1, 6001)]
    public async Task DenseMapAreaRetainsFeaturesAcrossTheWholeTile(double resolution, int lod, int featureCount)
    {
        MapFeature[] features = Enumerable.Range(1, featureCount).Select(id =>
        {
            double x = 10 + id % 60 * 6;
            double y = 10 + id / 60 * 5;
            return TestData.Polygon(id, id % 2 == 0 ? TerrainKind.Forest : TerrainKind.Lake,
                x, y, x + 2, y + 2);
        }).Append(TestData.Line(featureCount + 1, TerrainKind.Road, (10, 600), (350, 600)))
            .Append(TestData.Line(featureCount + 2, TerrainKind.Road, (1000, 1000), (1100, 1100))).ToArray();
        using var fixture = new SqliteGeodataFixture(features, lod < 0 ? null : lod);

        IReadOnlyList<MapFeature> displayed = await fixture.Repository.QueryMapAsync(
            fixture.Repository.Info.Bounds, resolution, default);

        Assert.Equal(featureCount + 1, displayed.Count);
        Assert.Contains(displayed, feature => feature.Id == featureCount + 1 && feature.Kind == TerrainKind.Road);
        Assert.DoesNotContain(displayed, feature => feature.Id == featureCount + 2);
        Assert.True(features.Take(featureCount + 1).Select(feature => feature.Id).ToHashSet()
            .SetEquals(displayed.Select(feature => feature.Id)));
    }

    [Fact]
    public async Task SimplifiedDisplayCannotRemoveAnObstacleFromRouting()
    {
        using var fixture = new SqliteGeodataFixture(TestData.Polygon(1, TerrainKind.Marsh, 151, -20, 153, 700));
        Persistence.SqliteGeodataRepository repository = fixture.Repository;
        Assert.Empty(await repository.QueryMapAsync(repository.Info.Bounds, 1500, default));
        Assert.Equal(TerrainKind.Marsh, Assert.Single(await repository.QueryAsync(repository.Info.Bounds, default)).Kind);

        var planner = new GridRoutePlanner(repository, new GridGenerator(), new AStarGridPathfinder(),
            NullLogger<GridRoutePlanner>.Instance);
        await Assert.ThrowsAsync<PlanningException>(() => planner.PlanAsync(
            new[] { TestData.P(50, 150), TestData.P(260, 150) }, TestData.Options, default));
    }
}
