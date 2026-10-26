using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class PointPlacementTests
{
    [Theory]
    [InlineData(TerrainKind.Building)]
    [InlineData(TerrainKind.Lake)]
    [InlineData(TerrainKind.Marsh)]
    public async Task AreaObstaclesRejectNewPointsWithoutChangingSession(TerrainKind kind)
    {
        var obstacle = TestData.Polygon(1, kind, 40, 140, 60, 160);
        var validator = new GeodataPointPlacementValidator(new MemoryData(obstacle));
        var session = new PlanningSession(TestData.Planner(), validator);

        var error = await Assert.ThrowsAsync<PlanningException>(
            () => session.AddValidatedAsync(TestData.P(50, 150), default));

        Assert.Contains("Нельзя установить точку", error.Message);
        Assert.Empty(session.Points);
        Assert.Equal(0, session.Revision);
    }

    [Fact]
    public async Task RiverLineRejectsPointOnItsSourceGeometry()
    {
        var river = TestData.Line(1, TerrainKind.River, (40, 150), (60, 150));
        var validator = new GeodataPointPlacementValidator(new MemoryData(river));

        var error = await Assert.ThrowsAsync<PlanningException>(
            () => validator.ValidateAsync(TestData.P(50, 150), default));

        Assert.Contains("русла реки", error.Message);
    }

    [Fact]
    public async Task BoundaryOfBlockedAreaIsRejected()
    {
        var building = TestData.Polygon(1, TerrainKind.Building, 40, 140, 60, 160);
        var validator = new GeodataPointPlacementValidator(new MemoryData(building));

        var error = await Assert.ThrowsAsync<PlanningException>(
            () => validator.ValidateAsync(TestData.P(40, 150), default));

        Assert.Contains("здания", error.Message);
    }

    [Fact]
    public async Task InvalidMoveKeepsExistingPointAndRevision()
    {
        var obstacle = TestData.Polygon(1, TerrainKind.Building, 90, 140, 110, 160);
        var session = new PlanningSession(
            TestData.Planner(),
            new GeodataPointPlacementValidator(new MemoryData(obstacle)));
        var point = session.Add(TestData.P(50, 150));
        var revision = session.Revision;

        await Assert.ThrowsAsync<PlanningException>(
            () => session.MoveValidatedAsync(point.Id, TestData.P(100, 150), default));

        Assert.Equal(point, Assert.Single(session.Points));
        Assert.Equal(revision, session.Revision);
    }

    [Fact]
    public async Task PointOutsidePackageIsRejected()
    {
        var validator = new GeodataPointPlacementValidator(new MemoryData());

        var error = await Assert.ThrowsAsync<PlanningException>(
            () => validator.ValidateAsync(new(55, 30), default));

        Assert.Contains("нет данных", error.Message);
    }
}
