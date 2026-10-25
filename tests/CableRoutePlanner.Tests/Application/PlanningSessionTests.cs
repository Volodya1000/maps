using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class PlanningSessionTests
{
    [Fact]
    public void PointMutationsValidateIdsAndIndexes()
    {
        var session = new PlanningSession(new Planner());
        var first = session.Add(new(53, 27));
        var second = session.Add(new(53.1, 27.1));

        session.Move(first.Id, new(53.2, 27.2));
        session.Remove(Guid.NewGuid());
        session.Reorder(1, 0);
        session.Remove(second.Id);
        session.Clear();

        Assert.Empty(session.Points);
        Assert.Throws<PlanningException>(() => session.Move(Guid.NewGuid(), new(53, 27)));
        Assert.Throws<PlanningException>(() => session.Reorder(-1, 0));
    }

    [Fact]
    public async Task BuildRejectsInvalidStateAndHonoursCancellation()
    {
        var session = new PlanningSession(new Planner());
        await Assert.ThrowsAsync<PlanningException>(() => session.BuildAsync(TestData.Options, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => session.BuildAsync(null!, default));

        session.Add(new(53, 27));
        session.Add(new(53.1, 27.1));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.BuildAsync(TestData.Options, canceled.Token));
    }

    [Fact]
    public void PointLimitIsEnforced()
    {
        var session = new PlanningSession(new Planner());
        for (var index = 0; index < 100; index++)
        {
            session.Add(new(53, 27));
        }

        Assert.Throws<PlanningException>(() => session.Add(new(53, 27)));
    }

    private sealed class Planner : IRoutePlanner
    {
        public Task<IReadOnlyList<RouteAlternative>> PlanAsync(
            IReadOnlyList<GeoCoordinate> points,
            RoutingOptions options,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RouteAlternative>>([]);
    }
}
