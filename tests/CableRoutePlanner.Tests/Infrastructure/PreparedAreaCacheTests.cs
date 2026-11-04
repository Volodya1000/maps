using System.Collections.Concurrent;
using System.Text.Json;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class PreparedAreaCacheTests
{
    [Theory]
    [InlineData("same")]
    [InlineData("gas")]
    [InlineData("cell")]
    [InlineData("both")]
    public async Task SettingsChangesReuseSpatialDataButMatchAFreshCalculation(string change)
    {
        MapFeature[] features = CrossingChoices();
        QueryRecordingRepository data = new(new MemoryData(features));
        GridRoutePlanner planner = Planner(data);
        GeoCoordinate[] points = [TestData.P(40, 100), TestData.P(280, 200)];
        RoutingOptions changed = ChangedOptions(change);
        IReadOnlyList<RouteAlternative> ordinary = await planner.PlanAsync(points, TestData.Options, default);
        IReadOnlyList<RouteAlternative> updated = await planner.PlanAsync(points, changed, default);
        IReadOnlyList<RouteAlternative> fresh = await Planner(new MemoryData(features)).PlanAsync(points, changed, default);

        Assert.NotEmpty(ordinary);
        Assert.NotEmpty(updated);
        Assert.Equal(Snapshot(fresh), Snapshot(updated));
        Assert.All(updated, route =>
        {
            Assert.True(route.Metrics.LengthMetres > 0);
            Assert.True(route.Metrics.WeightedCost >= 0);
            Assert.Equal(0, route.Metrics.ApprovedWaterCrossings);
        });
        if (changed.PreferOverheadGasCrossings)
        {
            Assert.Equal(3, Assert.Single(updated[0].GasPipelineCandidateIds!));
            Assert.Empty(ordinary[0].GasPipelineCandidateIds!);
            Assert.True(updated[0].Metrics.WeightedCost < ordinary.First(route => route.GasPipelineCandidateIds!.Contains(3)).Metrics.WeightedCost);
        }
        // These are actual repository query receipts, alongside complete output
        // comparisons. No route/evaluator/cache implementation is mocked.
        Assert.Equal(data.Info.Bounds, Assert.Single(data.Queries));
    }

    [Fact]
    public async Task WarmParallelSettingsRequestsKeepTheirOwnResultsWithoutReloadingTheArea()
    {
        MapFeature[] features = CrossingChoices();
        QueryRecordingRepository data = new(new MemoryData(features));
        GridRoutePlanner planner = Planner(data);
        GeoCoordinate[] points = [TestData.P(40, 100), TestData.P(280, 200)];
        _ = await planner.PlanAsync(points, TestData.Options, default);
        RoutingOptions[] settings = [ChangedOptions("same"), ChangedOptions("gas"), ChangedOptions("cell"), ChangedOptions("both")];
        IReadOnlyList<RouteAlternative>[] results = await Task.WhenAll(settings.Select(options => planner.PlanAsync(points, options, default)));

        for (int index = 0; index < settings.Length; index++)
        {
            IReadOnlyList<RouteAlternative> reference = await Planner(new MemoryData(features)).PlanAsync(points, settings[index], default);
            Assert.Equal(Snapshot(reference), Snapshot(results[index]));
        }
        Assert.Single(data.Queries);
    }

    [Fact]
    public async Task RepeatedBoundsReuseTheSnapshotButOnlyTheLastRegionIsRetained()
    {
        using SqliteGeodataFixture fixture = Regions();
        PreparedRoutingAreaCache cache = new(fixture.Repository);
        GeoBounds left = Bounds(0, 100), right = Bounds(200, 300);
        PreparedRoutingArea first = await cache.GetAsync(left, default);
        PreparedRoutingArea repeated = await cache.GetAsync(left, default);
        Assert.Same(first.Terrain, repeated.Terrain);
        Assert.Equal(0, repeated.QueryMilliseconds);
        Assert.Equal(0, repeated.PreparationMilliseconds);
        AssertBlockedLeft(first);

        PreparedRoutingArea second = await cache.GetAsync(right, default);
        Assert.NotSame(first.Terrain, second.Terrain);
        Assert.Equal(right, second.Bounds);
        AssertForestedRight(second);
        PreparedRoutingArea back = await cache.GetAsync(left, default);
        Assert.NotSame(first.Terrain, back.Terrain);
        AssertBlockedLeft(back);
    }

    [Fact]
    public async Task ConcurrentColdRegionsNeverExchangePreparedGeometry()
    {
        using SqliteGeodataFixture fixture = Regions();
        PreparedRoutingAreaCache cache = new(fixture.Repository);
        PreparedRoutingArea[] results = await Task.WhenAll(
            cache.GetAsync(Bounds(0, 100), default), cache.GetAsync(Bounds(200, 300), default));

        Assert.NotSame(results[0].Terrain, results[1].Terrain);
        AssertBlockedLeft(results[0]);
        AssertForestedRight(results[1]);
    }

    [Fact]
    public async Task CancellationDuringPreparationDoesNotReplaceTheLastSuccessfulArea()
    {
        using SqliteGeodataFixture fixture = Regions();
        PausingRepository data = new(fixture.Repository, Bounds(200, 300));
        PreparedRoutingAreaCache cache = new(data);
        PreparedRoutingArea retained = await cache.GetAsync(Bounds(0, 100), default);
        using CancellationTokenSource cancellation = new();
        Task<PreparedRoutingArea> pending = cache.GetAsync(Bounds(200, 300), cancellation.Token);
        await data.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        PreparedRoutingArea after = await cache.GetAsync(Bounds(0, 100), default);
        Assert.Same(retained.Terrain, after.Terrain);
        AssertBlockedLeft(after);

        data.Resume.TrySetResult();
        PreparedRoutingArea completed = await cache.GetAsync(Bounds(200, 300), default);
        AssertForestedRight(completed);
    }

    [Fact]
    public async Task CancelledCacheHitDoesNotPublishASuccessfulResult()
    {
        PreparedRoutingAreaCache cache = new(new MemoryData());
        GeoBounds bounds = new MemoryData().Info.Bounds;
        _ = await cache.GetAsync(bounds, default);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync(bounds, cancellation.Token));
    }

    [Fact]
    public async Task DifferentRepositoryOwnersCannotShareAnAreaOrAPlannerResult()
    {
        MemoryData blocked = new(TestData.Polygon(1, TerrainKind.Building, 120, 80, 180, 240));
        MemoryData empty = new();
        PreparedRoutingArea first = await new PreparedRoutingAreaCache(blocked).GetAsync(blocked.Info.Bounds, default);
        PreparedRoutingArea second = await new PreparedRoutingAreaCache(empty).GetAsync(empty.Info.Bounds, default);
        Assert.NotSame(first.Terrain, second.Terrain);
        GeoCoordinate[] points = [TestData.P(50, 150), TestData.P(280, 150)];
        IReadOnlyList<RouteAlternative> detour = await Planner(blocked).PlanAsync(points, TestData.Options, default);
        IReadOnlyList<RouteAlternative> direct = await Planner(empty).PlanAsync(points, TestData.Options, default);
        Assert.True(detour[0].Metrics.LengthMetres > direct[0].Metrics.LengthMetres + 1);
        Assert.Equal(points[0].DistanceTo(points[1]), direct[0].Metrics.LengthMetres, 6);
    }

    private static SqliteGeodataFixture Regions() => new(
        TestData.Polygon(11, TerrainKind.Marsh, 20, 20, 80, 80),
        TestData.Polygon(22, TerrainKind.Forest, 220, 20, 280, 80));

    private static GeoBounds Bounds(double west, double east) => GeoBounds.Around([TestData.P(west, 0), TestData.P(east, 100)]);

    private static void AssertBlockedLeft(PreparedRoutingArea area)
    {
        TerrainEvaluator evaluator = new(area.Terrain);
        Assert.Null(evaluator.Evaluate(area.Projection.Forward(TestData.P(10, 50)), area.Projection.Forward(TestData.P(90, 50))));
    }

    private static void AssertForestedRight(PreparedRoutingArea area)
    {
        TerrainEvaluator evaluator = new(area.Terrain);
        EdgeEvaluation facts = Assert.IsType<EdgeEvaluation>(evaluator.Evaluate(
            area.Projection.Forward(TestData.P(210, 50)), area.Projection.Forward(TestData.P(290, 50))));
        Assert.Equal(TestData.P(220, 50).DistanceTo(TestData.P(280, 50)), facts.Forest, 6);
    }

    private static GridRoutePlanner Planner(IGeodataRepository data) =>
        new(data, new GridGenerator(), new AStarGridPathfinder(), NullLogger<GridRoutePlanner>.Instance);

    private static RoutingOptions ChangedOptions(string change) => TestData.Options with
    {
        PreferOverheadGasCrossings = change is "gas" or "both",
        CellSizeMetres = change is "cell" or "both" ? 40 : TestData.Options.CellSizeMetres
    };

    private static MapFeature[] CrossingChoices() =>
    [
        TestData.Line(1, TerrainKind.Road, (-100, 150), (700, 150)),
        TestData.Line(2, TerrainKind.BridgeCandidate, (120, 130), (120, 170)),
        TestData.Line(3, TerrainKind.GasPipelineCrossingCandidate, (220, 130), (220, 170))
    ];

    private static string Snapshot(IEnumerable<RouteAlternative> routes) => JsonSerializer.Serialize(routes.Select(route => new
    {
        route.Name,
        route.Geometry,
        route.Metrics,
        route.TransitionPoints,
        route.RoadContacts,
        route.GasPipelineCandidateIds
    }));

    private sealed class QueryRecordingRepository(IGeodataRepository inner) : IGeodataRepository
    {
        private readonly ConcurrentQueue<GeoBounds> _queries = new();
        public IReadOnlyCollection<GeoBounds> Queries => _queries.ToArray();
        public DataPackageInfo Info => inner.Info;
        public Task<IReadOnlyList<MapFeature>> QueryAsync(GeoBounds bounds, CancellationToken ct)
        {
            _queries.Enqueue(bounds);
            return inner.QueryAsync(bounds, ct);
        }
    }

    private sealed class PausingRepository(IGeodataRepository inner, GeoBounds paused) : IGeodataRepository
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DataPackageInfo Info => inner.Info;
        public async Task<IReadOnlyList<MapFeature>> QueryAsync(GeoBounds bounds, CancellationToken ct)
        {
            IReadOnlyList<MapFeature> result = await inner.QueryAsync(bounds, ct);
            if (bounds == paused)
            {
                Started.TrySetResult();
                await Resume.Task.WaitAsync(ct);
            }
            return result;
        }
    }
}
