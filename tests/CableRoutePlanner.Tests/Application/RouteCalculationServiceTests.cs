using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class RouteCalculationServiceTests
{
    [Fact]
    public async Task ReplacementDiscardsOlderResultEvenWhenPlannerIgnoresCancellation()
    {
        var planner = new DeferredPlanner();
        PlanningSession session = await CreateSession(planner);
        using RouteCalculationService service = CreateService(session);
        Task<RouteCalculationResult?> first = service.BuildAsync();
        Task firstCompletion = service.Completion;
        Task<RouteCalculationResult?> second = service.BuildAsync();
        Assert.True(service.IsBusy);
        planner.Requests[0].SetResult([]);
        Assert.Null(await first);
        await firstCompletion;
        Assert.True(service.IsBusy);
        planner.Requests[1].SetResult([]);
        RouteCalculationResult result = Assert.IsType<RouteCalculationResult>(await second);
        Assert.Equal(RouteCalculationOutcome.Completed, result.Outcome);
        Assert.True(service.IsCurrent(result));
        Assert.False(service.IsBusy);
        await service.Completion;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationFinishesWithoutPublishingRoutes(bool invalidate)
    {
        var planner = new DeferredPlanner();
        PlanningSession session = await CreateSession(planner);
        using RouteCalculationService service = CreateService(session);
        Task<RouteCalculationResult?> build = service.BuildAsync();
        if (invalidate)
        { service.Invalidate(); }
        else
        { service.Cancel(); }
        planner.Requests[0].SetResult([]);
        RouteCalculationResult? result = await build;
        if (invalidate)
        { Assert.Null(result); }
        else
        { Assert.Equal(RouteCalculationOutcome.Cancelled, result?.Outcome); }
        Assert.False(service.IsBusy);
        await service.Completion;
    }

    [Fact]
    public async Task DisposalPreventsLateResultAndNewOperations()
    {
        var planner = new DeferredPlanner();
        PlanningSession session = await CreateSession(planner);
        RouteCalculationService service = CreateService(session);
        Task<RouteCalculationResult?> build = service.BuildAsync();
        service.Dispose();
        service.Dispose();
        planner.Requests[0].SetResult([]);
        Assert.Null(await build);
        Assert.False(service.IsBusy);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.BuildAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.MovePointAsync(session.Points[0].Id, TestData.P(40, 40)));
    }

    [Fact]
    public async Task FailureReturnsErrorAndReleasesBusyState()
    {
        var planner = new DeferredPlanner();
        PlanningSession session = await CreateSession(planner);
        using RouteCalculationService service = CreateService(session);
        Task<RouteCalculationResult?> build = service.BuildAsync();
        planner.Requests[0].SetException(new PlanningException("cannot plan"));
        RouteCalculationResult result = Assert.IsType<RouteCalculationResult>(await build);
        Assert.Equal(RouteCalculationOutcome.Failed, result.Outcome);
        Assert.Empty(result.Routes);
        Assert.False(service.IsBusy);
    }

    [Fact]
    public async Task MoveChecksPackageBoundsAndPreservesPointOnFailure()
    {
        PlanningSession session = await CreateSession(TestData.Planner());
        using RouteCalculationService service = CreateService(session);
        Waypoint point = session.Points[0];
        PointMoveResult rejected = Assert.IsType<PointMoveResult>(await service.MovePointAsync(point.Id, new GeoCoordinate(0, 0)));
        Assert.False(rejected.Success);
        Assert.Equal(PlanningErrorReason.PointOutsidePackage, rejected.Error?.Reason);
        Assert.Equal(point, session.Points[0]);
        GeoCoordinate moved = TestData.P(40, 40);
        Assert.True((await service.MovePointAsync(point.Id, moved))?.Success);
        Assert.Equal(moved, session.Points[0].Coordinate);
    }

    [Fact]
    public async Task NewerMoveWinsWhenOlderValidationFinishesLast()
    {
        var validator = new DeferredValidator();
        var session = new PlanningSession(TestData.Planner(), validator);
        Waypoint point = await session.AddAsync(TestData.P(20, 20), CancellationToken.None);
        validator.Defer = true;
        using RouteCalculationService service = CreateService(session);
        Task<PointMoveResult?> first = service.MovePointAsync(point.Id, TestData.P(40, 40));
        GeoCoordinate latest = TestData.P(60, 60);
        Task<PointMoveResult?> second = service.MovePointAsync(point.Id, latest);
        validator.Requests[1].SetResult();
        Assert.True((await second)?.Success);
        validator.Requests[0].SetResult();
        Assert.Null(await first);
        Assert.Equal(latest, session.Points[0].Coordinate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidationOrDisposalCancelsPendingMove(bool dispose)
    {
        var validator = new DeferredValidator();
        var session = new PlanningSession(TestData.Planner(), validator);
        Waypoint point = await session.AddAsync(TestData.P(20, 20), CancellationToken.None);
        validator.Defer = true;
        using RouteCalculationService service = CreateService(session);
        Task<PointMoveResult?> move = service.MovePointAsync(point.Id, TestData.P(40, 40));
        if (dispose)
        { service.Dispose(); }
        else
        { service.Invalidate(); }
        validator.Requests[0].SetResult();
        Assert.Null(await move);
        Assert.Equal(point, session.Points[0]);
    }

    private sealed class DeferredValidator : IPointPlacementValidator
    {
        public bool Defer { get; set; }
        public List<TaskCompletionSource> Requests { get; } = [];
        public Task ValidateAsync(GeoCoordinate coordinate, CancellationToken cancellationToken)
        {
            if (!Defer)
            { return Task.CompletedTask; }
            var request = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add(request);
            return request.Task;
        }
    }

    private static RouteCalculationService CreateService(IPlanningSession session) =>
        new(session, new MemorySettings(), new MemoryData().Info.Bounds);

    private static async Task<PlanningSession> CreateSession(IRoutePlanner planner)
    {
        var session = new PlanningSession(planner, TestData.AllowAllValidator);
        await session.AddAsync(TestData.P(20, 20), CancellationToken.None);
        await session.AddAsync(TestData.P(100, 100), CancellationToken.None);
        return session;
    }

    private sealed class DeferredPlanner : IRoutePlanner
    {
        public List<TaskCompletionSource<IReadOnlyList<RouteAlternative>>> Requests { get; } = [];

        public Task<IReadOnlyList<RouteAlternative>> PlanAsync(IReadOnlyList<GeoCoordinate> points,
            RoutingOptions options, CancellationToken cancellationToken)
        {
            var request = new TaskCompletionSource<IReadOnlyList<RouteAlternative>>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add(request);
            return request.Task;
        }
    }
}
