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
        MapFeature obstacle = TestData.Polygon(1, kind, 40, 140, 60, 160);
        var validator = new GeodataPointPlacementValidator(new MemoryData(obstacle));
        var session = new PlanningSession(TestData.Planner(), validator);

        PlanningException error = await Assert.ThrowsAsync<PlanningException>(
            () => ((IPlanningSession)session).AddAsync(TestData.P(50, 150), default));

        Assert.Contains("Нельзя установить точку", error.Message);
        Assert.Empty(session.Points);
        Assert.Equal(0, session.Revision);
    }

    [Fact]
    public async Task RiverLineRejectsPointOnItsSourceGeometry()
    {
        MapFeature river = TestData.Line(1, TerrainKind.River, (40, 150), (60, 150));
        var validator = new GeodataPointPlacementValidator(new MemoryData(river));

        PlanningException error = await Assert.ThrowsAsync<PlanningException>(
            () => validator.ValidateAsync(TestData.P(50, 150), default));

        Assert.Contains("русла реки", error.Message);
    }

    [Fact]
    public async Task LinearRiverUsesDocumentedCoordinateUncertaintyOnly()
    {
        MapFeature river = TestData.Line(1, TerrainKind.River, (40, 150), (60, 150));
        var validator = new GeodataPointPlacementValidator(new MemoryData(river));
        await Assert.ThrowsAsync<PlanningException>(
            () => validator.ValidateAsync(TestData.P(50, 150.5), default));
        await validator.ValidateAsync(TestData.P(50, 152), default);
    }

    [Fact]
    public async Task BoundaryOfBlockedAreaIsRejected()
    {
        MapFeature building = TestData.Polygon(1, TerrainKind.Building, 40, 140, 60, 160);
        var validator = new GeodataPointPlacementValidator(new MemoryData(building));

        PlanningException error = await Assert.ThrowsAsync<PlanningException>(
            () => validator.ValidateAsync(TestData.P(40, 150), default));

        Assert.Contains("здания", error.Message);
    }

    [Fact]
    public async Task InvalidMoveKeepsExistingPointAndRevision()
    {
        MapFeature obstacle = TestData.Polygon(1, TerrainKind.Building, 90, 140, 110, 160);
        var session = new PlanningSession(
            TestData.Planner(),
            new GeodataPointPlacementValidator(new MemoryData(obstacle)));
        Waypoint point = await session.AddAsync(TestData.P(50, 150), default);
        long revision = session.Revision;

        await Assert.ThrowsAsync<PlanningException>(
            () => session.MoveAsync(point.Id, TestData.P(100, 150), default));

        Assert.Equal(point, Assert.Single(session.Points));
        Assert.Equal(revision, session.Revision);
    }

    [Fact]
    public async Task PointOutsidePackageIsRejected()
    {
        var validator = new GeodataPointPlacementValidator(new MemoryData());

        PlanningException error = await Assert.ThrowsAsync<PlanningException>(
            () => validator.ValidateAsync(new(55, 30), default));

        Assert.Contains("нет данных", error.Message);
    }
}
