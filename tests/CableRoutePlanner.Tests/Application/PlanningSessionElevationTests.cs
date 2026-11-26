using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class PlanningSessionElevationTests
{
    [Fact]
    public async Task BuildEnrichesEveryAlternativeBeforeReturning()
    {
        PlanningSession session = await CreateSession(new FlatAnalyzer());

        IReadOnlyList<RouteAlternative> routes = await session.BuildAsync(TestData.Options, default);

        Assert.Collection(routes,
            route => AssertEnriched(route, "First"),
            route => AssertEnriched(route, "Second"));
    }

    [Fact]
    public async Task PointEditWhileElevationIsPendingRejectsStaleRoutes()
    {
        BlockingAnalyzer analyzer = new();
        PlanningSession session = await CreateSession(analyzer);
        Task<IReadOnlyList<RouteAlternative>> pending = session.BuildAsync(TestData.Options, default);
        await analyzer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        session.Reorder(1, 0);
        analyzer.Release.SetResult();

        await Assert.ThrowsAsync<PlanningException>(() => pending);
        Assert.Equal(new GeoCoordinate(53, 27.001), session.Points[0].Coordinate);
    }

    [Fact]
    public async Task CancellationWhileElevationIsPendingRejectsCompletedAnalysis()
    {
        BlockingAnalyzer analyzer = new();
        PlanningSession session = await CreateSession(analyzer);
        using CancellationTokenSource cancellation = new();
        Task<IReadOnlyList<RouteAlternative>> pending = session.BuildAsync(TestData.Options, cancellation.Token);
        await analyzer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        analyzer.Release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private static void AssertEnriched(RouteAlternative route, string name)
    {
        Assert.Equal(name, route.Name);
        Assert.True(route.ElevationProfile?.HasCompleteCoverage);
    }

    private static async Task<PlanningSession> CreateSession(IRouteElevationAnalyzer analyzer)
    {
        PlanningSession session = new(new MultipleRoutePlanner(), TestData.AllowAllValidator, analyzer);
        await session.AddAsync(new(53, 27), default);
        await session.AddAsync(new(53, 27.001), default);
        return session;
    }

    private sealed class MultipleRoutePlanner : IRoutePlanner
    {
        public Task<IReadOnlyList<RouteAlternative>> PlanAsync(
            IReadOnlyList<GeoCoordinate> points, RoutingOptions options, CancellationToken cancellationToken)
        {
            RouteMetrics metrics = new(100, 0, 0, 0, 0, 0, 100, null);
            return Task.FromResult<IReadOnlyList<RouteAlternative>>([
                new(Guid.NewGuid(), "First", points, metrics),
                new(Guid.NewGuid(), "Second", points, metrics)
            ]);
        }
    }

    private sealed class FlatAnalyzer : IRouteElevationAnalyzer
    {
        public Task<RouteAlternative> AnalyzeAsync(RouteAlternative route, CancellationToken cancellationToken) =>
            Task.FromResult(route.WithElevationSamples(
                new("Test data", "1", "EGM2008", 30, "Test", new Uri("https://example.com/license")),
                [new(route.Geometry[0], 0, 50, 0), new(route.Geometry[^1], 100, 50, 0)], 100));
    }

    private sealed class BlockingAnalyzer : IRouteElevationAnalyzer
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RouteAlternative> AnalyzeAsync(RouteAlternative route, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task;
            return route;
        }
    }
}
