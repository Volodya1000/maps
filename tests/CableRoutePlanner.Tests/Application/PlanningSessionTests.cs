using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class PlanningSessionTests
{
    [Fact]
    public void SessionRequiresPlacementValidator() =>
        Assert.Throws<ArgumentNullException>(() => new PlanningSession(new Planner(), null!));

    [Fact]
    public async Task PointMutationsValidateIdsAndIndexes()
    {
        var session = new PlanningSession(new Planner(), TestData.AllowAllValidator);
        Waypoint first = await session.AddAsync(new(53, 27), default);
        Waypoint second = await session.AddAsync(new(53.1, 27.1), default);

        await session.MoveAsync(first.Id, new(53.2, 27.2), default);
        session.Remove(Guid.NewGuid());
        session.Reorder(1, 0);
        session.Remove(second.Id);
        session.Clear();

        Assert.Empty(session.Points);
        await Assert.ThrowsAsync<PlanningException>(() => session.MoveAsync(Guid.NewGuid(), new(53, 27), default));
        Assert.Throws<PlanningException>(() => session.Reorder(-1, 0));
    }

    [Fact]
    public async Task BuildRejectsInvalidStateAndHonoursCancellation()
    {
        var session = new PlanningSession(new Planner(), TestData.AllowAllValidator);
        await Assert.ThrowsAsync<PlanningException>(() => session.BuildAsync(TestData.Options, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => session.BuildAsync(null!, default));

        await session.AddAsync(new(53, 27), default);
        await session.AddAsync(new(53.1, 27.1), default);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.BuildAsync(TestData.Options, canceled.Token));
    }

    [Fact]
    public async Task PointLimitIsEnforced()
    {
        var session = new PlanningSession(new Planner(), TestData.AllowAllValidator);
        for (int index = 0; index < 100; index++)
        {
            await session.AddAsync(new(53, 27), default);
        }

        await Assert.ThrowsAsync<PlanningException>(() => session.AddAsync(new(53, 27), default));
    }

    [Fact]
    public async Task CancelledPlacementCannotMutatePublicSessionContract()
    {
        var validator = new BlockingPlacementValidator();
        var session = new PlanningSession(new Planner(), validator);
        Waypoint first = await session.AddAsync(new(53, 27), default);
        long revision = session.Revision;
        using var cancellation = new CancellationTokenSource();
        Task move = ((IPlanningSession)session).MoveAsync(first.Id, new(53.1, 27.1), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.Equal(revision, session.Revision);
        Assert.Equal(first, Assert.Single(session.Points));
    }

    private sealed class BlockingPlacementValidator : IPointPlacementValidator
    {
        private int _requests;

        public Task ValidateAsync(GeoCoordinate coordinate, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _requests) == 1
                ? Task.CompletedTask
                : Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private sealed class Planner : IRoutePlanner
    {
        public Task<IReadOnlyList<RouteAlternative>> PlanAsync(
            IReadOnlyList<GeoCoordinate> points,
            RoutingOptions options,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RouteAlternative>>([]);
    }
}
