using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task SimplifiedDisplayCannotRemoveAnObstacleFromRouting()
    {
        using var fixture = new SqliteGeodataFixture(TestData.Polygon(1, TerrainKind.Marsh, 151, -20, 153, 700));
        var repository = fixture.Repository;
        Assert.Empty(await repository.QueryMapAsync(repository.Info.Bounds, 1500, default));
        Assert.Equal(TerrainKind.Marsh, Assert.Single(await repository.QueryAsync(repository.Info.Bounds, default)).Kind);

        var planner = new GridRoutePlanner(repository, new GridGenerator(), new AStarGridPathfinder(),
            NullLogger<GridRoutePlanner>.Instance);
        await Assert.ThrowsAsync<PlanningException>(() => planner.PlanAsync(
            new[] { TestData.P(50, 150), TestData.P(260, 150) }, TestData.Options, default));
    }
}
