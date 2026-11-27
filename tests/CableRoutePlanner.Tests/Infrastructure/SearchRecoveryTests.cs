using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Routing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class SearchRecoveryTests
{
    [Fact]
    public async Task RecoveryCannotCrossAForbiddenRoadSpanningEverySearchArea()
    {
        MapFeature road = TestData.Line(1, TerrainKind.Road, (175, -20_000), (175, 20_000));
        GeoCoordinate southwest = TestData.P(-6_000, -6_000);
        GeoCoordinate northeast = TestData.P(6_500, 6_500);
        MemoryData data = new(road)
        {
            Info = new("synthetic-road-recovery",
                new(southwest.Longitude, southwest.Latitude, northeast.Longitude, northeast.Latitude),
                "Synthetic fixture", false, "tests only")
        };
        RoutingTestLogger logger = new();
        GridRoutePlanner planner = new(data, new GridGenerator(), new AStarGridPathfinder(), logger);
        RoutingOptions options = new(200, 2_000);

        PlanningException error = await Assert.ThrowsAsync<PlanningException>(() => planner.PlanAsync(
            [TestData.P(50, 150), TestData.P(300, 150)], options, default));

        Assert.Equal(PlanningErrorReason.NoRouteInSearchArea, error.Reason);
        Assert.NotEmpty(error.SearchFailures);
        Assert.All(error.SearchFailures, failure =>
            Assert.Equal(PlanningErrorReason.NoRouteInSearchArea, failure.Error.Reason));
        Assert.Contains(logger.Events, entry => entry.Event.Id == 8
            && Assert.IsType<bool>(entry.Fields["SurfaceRecovery"]));
        RoutingLogEntry[] surface = logger.Events.Where(entry => entry.Event.Id == 9).ToArray();
        Assert.NotEmpty(surface);
        Assert.All(surface, entry =>
        {
            Assert.False(Assert.IsType<bool>(entry.Fields["Found"]));
            Assert.InRange(Assert.IsType<int>(entry.Fields["NodeCount"]), 0, options.MaxNodes);
        });
        Assert.DoesNotContain(logger.Events, entry => entry.Event.Id is 2 or 3);
    }

    [Fact]
    public async Task ProvenRouteLengthLimitRetainsStructuredProofWithoutRecovery()
    {
        GeoCoordinate[] points = [TestData.P(50, 150), TestData.P(300, 150)];
        RoutingOptions options = TestData.Options with { MaxRouteLengthMetres = 100 };
        RoutingTestLogger logger = new();
        GridRoutePlanner planner = new(new MemoryData(), new GridGenerator(), new AStarGridPathfinder(), logger);

        PlanningException error = await Assert.ThrowsAsync<PlanningException>(() =>
            planner.PlanAsync(points, options, default));

        Assert.Equal(PlanningErrorReason.RouteLengthLimitExceeded, error.Reason);
        Assert.NotNull(error.ActualRouteLengthMetres);
        Assert.Equal(points[0].DistanceTo(points[1]), error.ActualRouteLengthMetres.Value, precision: 6);
        Assert.Equal(options.MaxRouteLengthMetres, error.MaximumRouteLengthMetres);
        Assert.NotEmpty(error.SearchFailures);
        Assert.All(error.SearchFailures, failure =>
        {
            Assert.Equal(PlanningErrorReason.RouteLengthLimitExceeded, failure.Error.Reason);
            Assert.Equal(error.ActualRouteLengthMetres, failure.Error.ActualRouteLengthMetres);
            Assert.Equal(options.MaxRouteLengthMetres, failure.Error.MaximumRouteLengthMetres);
        });
        AssertPrimaryAttemptOnly(logger);
        Assert.DoesNotContain(logger.Events, entry => entry.Event.Id is 2 or 3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAtSurfaceRecoveryPropagatesAndTheSamePlannerStartsFresh(bool insideSearch)
    {
        using SourceRoutingCaseFixture fixture = new("mass-gomel-bridge-03");
        using CancellationTokenSource cancellation = new();
        RoutingTestLogger captured = new();
        CancelOnSurfaceRecoveryLogger logger = new(captured, cancellation, insideSearch);
        IGridPathfinder pathfinder = insideSearch ? logger : new AStarGridPathfinder();
        GridRoutePlanner planner = new(fixture.Repository, new GridGenerator(), pathfinder, logger);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            planner.PlanAsync(fixture.Points, fixture.Options, cancellation.Token));

        RoutingLogEntry cancelledAttempt = Assert.Single(captured.Events, entry => entry.Event.Id == 8
            && Assert.IsType<bool>(entry.Fields["SurfaceRecovery"]));
        Guid cancelledId = Assert.IsType<Guid>(cancelledAttempt.Fields["CalculationId"]);
        Assert.True(cancellation.IsCancellationRequested);
        if (insideSearch)
        {
            RoutingLogEntry outcome = Assert.Single(captured.Events, entry => entry.Event.Id == 7
                && Assert.IsType<bool>(entry.Fields["Cancelled"]));
            Assert.Equal(cancelledId, outcome.Fields["CalculationId"]);
            Assert.Null(outcome.Fields["Reason"]);
        }
        Assert.DoesNotContain(captured.Events, entry => entry.Event.Id == 2);
        captured.Events.Clear();

        IReadOnlyList<RouteAlternative> routes = await planner.PlanAsync(fixture.Points, fixture.Options, default);

        Assert.NotEmpty(routes);
        Assert.All(routes, route =>
        {
            Assert.Equal(fixture.Points[0], route.Geometry[0]);
            Assert.Equal(fixture.Points[^1], route.Geometry[^1]);
        });
        RoutingLogEntry firstAttempt = captured.Events.First(entry => entry.Event.Id == 8);
        Assert.Equal(0, Assert.IsType<int>(firstAttempt.Fields["Attempt"]));
        Guid freshId = Assert.IsType<Guid>(firstAttempt.Fields["CalculationId"]);
        Assert.NotEqual(cancelledId, freshId);
        Assert.All(captured.Events.Where(entry => entry.Event.Id is >= 5 and <= 9), entry =>
            Assert.Equal(freshId, Assert.IsType<Guid>(entry.Fields["CalculationId"])));
        RoutingLogEntry completed = Assert.Single(captured.Events, entry => entry.Event.Id == 2);
        Assert.Equal(routes.Count, Assert.IsType<int>(completed.Fields["RouteCount"]));
    }

    [Fact]
    public async Task SurfaceInputBudgetIsReportedWithoutOpeningAForbiddenRoad()
    {
        const int maximumNodes = 1_000;
        (double X, double Y)[] vertices = Enumerable.Range(0, maximumNodes * 4 + 1)
            .Select(index => (175d, -20_000d + index * 10)).ToArray();
        MapFeature road = TestData.Line(1, TerrainKind.Road, vertices);
        RoutingTestLogger logger = new();
        GridRoutePlanner planner = new(new MemoryData(road), new GridGenerator(), new AStarGridPathfinder(), logger);

        PlanningException error = await Assert.ThrowsAsync<PlanningException>(() => planner.PlanAsync(
            [TestData.P(50, 150), TestData.P(300, 150)], new(200, maximumNodes), default));

        Assert.Equal(PlanningErrorReason.NoRouteInSearchArea, error.Reason);
        RoutingLogEntry[] surface = logger.Events.Where(entry => entry.Event.Id == 9).ToArray();
        Assert.NotEmpty(surface);
        Assert.All(surface, entry =>
        {
            Assert.True(Assert.IsType<bool>(entry.Fields["ResourceLimited"]));
            Assert.True(Assert.IsType<int>(entry.Fields["InputVertexCount"]) > maximumNodes * 4);
            Assert.Equal(0, Assert.IsType<int>(entry.Fields["NodeCount"]));
            Assert.False(Assert.IsType<bool>(entry.Fields["Found"]));
        });
        Assert.DoesNotContain(logger.Events, entry => entry.Event.Id is 2 or 3);
    }

    [Fact]
    public async Task RepeatedRealRecoveryPreservesGeometryMetricsAndBoundedCorrelatedAttempts()
    {
        using SourceRoutingCaseFixture fixture = new("mass-naliboki-bridge-02");
        RoutingTestLogger logger = new();
        GridRoutePlanner planner = new(fixture.Repository, new GridGenerator(), new AStarGridPathfinder(), logger);

        IReadOnlyList<RouteAlternative> first = await planner.PlanAsync(fixture.Points, fixture.Options, default);
        RoutingLogEntry[] firstEvents = logger.Events.ToArray();
        logger.Events.Clear();
        IReadOnlyList<RouteAlternative> repeated = await planner.PlanAsync(fixture.Points, fixture.Options, default);

        Assert.NotEmpty(first);
        Assert.Equal(first.Count, repeated.Count);
        for (int index = 0; index < first.Count; index++)
        {
            Assert.Equal(first[index].Name, repeated[index].Name);
            Assert.Equal(first[index].Geometry, repeated[index].Geometry);
            Assert.Equal(first[index].Metrics, repeated[index].Metrics);
            Assert.Equal(fixture.Points[0], repeated[index].Geometry[0]);
            Assert.Equal(fixture.Points[^1], repeated[index].Geometry[^1]);
        }
        Guid firstId = AssertRecoveryDiagnostics(firstEvents, fixture.Options);
        Guid repeatedId = AssertRecoveryDiagnostics(logger.Events.ToArray(), fixture.Options);
        Assert.NotEqual(firstId, repeatedId);
    }

    [Fact]
    public async Task PrimaryDirectSuccessKeepsOriginalGeometryAndUsesOneAttempt()
    {
        GeoCoordinate[] points = [TestData.P(50, 150), TestData.P(300, 150)];
        RoutingTestLogger logger = new();
        GridRoutePlanner planner = new(new MemoryData(), new GridGenerator(), new AStarGridPathfinder(), logger);

        IReadOnlyList<RouteAlternative> routes = await planner.PlanAsync(points, TestData.Options, default);

        RouteAlternative route = Assert.Single(routes);
        Assert.Equal(points, route.Geometry);
        Assert.Equal(points[0].DistanceTo(points[1]), route.Metrics.LengthMetres, precision: 6);
        AssertPrimaryAttemptOnly(logger);
        RoutingLogEntry completed = Assert.Single(logger.Events, entry => entry.Event.Id == 2);
        Assert.Equal(1, Assert.IsType<int>(completed.Fields["RouteCount"]));
    }

    private static void AssertPrimaryAttemptOnly(RoutingTestLogger logger)
    {
        RoutingLogEntry attempt = Assert.Single(logger.Events, entry => entry.Event.Id == 8);
        Assert.Equal(0, Assert.IsType<int>(attempt.Fields["Attempt"]));
        Assert.False(Assert.IsType<bool>(attempt.Fields["SurfaceRecovery"]));
        Assert.Single(logger.Events, entry => entry.Event.Id == 5);
        Assert.DoesNotContain(logger.Events, entry => entry.Event.Id == 9);
    }

    private static Guid AssertRecoveryDiagnostics(RoutingLogEntry[] entries, RoutingOptions options)
    {
        RoutingLogEntry[] attempts = entries.Where(entry => entry.Event.Id == 8).ToArray();
        Assert.True(attempts.Length > 1);
        Guid calculationId = Assert.IsType<Guid>(attempts[0].Fields["CalculationId"]);
        Assert.NotEqual(Guid.Empty, calculationId);
        Assert.Equal(Enumerable.Range(0, attempts.Length),
            attempts.Select(entry => Assert.IsType<int>(entry.Fields["Attempt"])));
        Assert.False(Assert.IsType<bool>(attempts[0].Fields["SurfaceRecovery"]));
        Assert.All(attempts.Skip(1), entry => Assert.True(Assert.IsType<bool>(entry.Fields["SurfaceRecovery"])));
        Assert.All(entries.Where(entry => entry.Event.Id is >= 5 and <= 9), entry =>
            Assert.Equal(calculationId, Assert.IsType<Guid>(entry.Fields["CalculationId"])));

        RoutingLogEntry[] meshes = entries.Where(entry => entry.Event.Id == 5).ToArray();
        Assert.Equal(attempts.Select(entry => Assert.IsType<int>(entry.Fields["Attempt"])),
            meshes.Select(entry => Assert.IsType<int>(entry.Fields["Attempt"])));
        Assert.All(meshes, entry =>
            Assert.InRange(Assert.IsType<int>(entry.Fields["NodeCount"]), 1, options.MaxNodes));
        Assert.Equal(options.CellSizeMetres, Assert.IsType<double>(meshes[0].Fields["RequestedCellSizeMetres"]));
        Assert.All(meshes.Skip(1), entry => Assert.Equal(RoutingOptions.MinCellSizeMetres,
            Assert.IsType<double>(entry.Fields["RequestedCellSizeMetres"])));

        RoutingLogEntry[] surface = entries.Where(entry => entry.Event.Id == 9).ToArray();
        Assert.NotEmpty(surface);
        Assert.All(surface, entry =>
            Assert.InRange(Assert.IsType<int>(entry.Fields["NodeCount"]), 0, options.MaxNodes));
        Assert.Contains(surface, entry => Assert.IsType<bool>(entry.Fields["Found"]));
        Assert.Single(entries, entry => entry.Event.Id == 2);
        return calculationId;
    }

    private sealed class CancelOnSurfaceRecoveryLogger(
        RoutingTestLogger captured,
        CancellationTokenSource cancellation,
        bool cancelInsideSearch) : ILogger<GridRoutePlanner>, IGridPathfinder
    {
        private bool _hasCancelled;
        private bool _recoveryStarted;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => captured.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => captured.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            captured.Log(logLevel, eventId, state, exception, formatter);
            if (eventId.Id == 8)
            {
                _recoveryStarted = captured.Events[^1].Fields["SurfaceRecovery"] is true;
            }
            if (!_hasCancelled && _recoveryStarted && !cancelInsideSearch)
            {
                _hasCancelled = true;
                cancellation.Cancel();
            }
        }

        public IReadOnlyList<int>? FindPath(IRoutingGraph graph, CancellationToken cancellationToken)
        {
            if (!_hasCancelled && _recoveryStarted && cancelInsideSearch)
            {
                _hasCancelled = true;
                cancellation.Cancel();
            }
            return new AStarGridPathfinder().FindPath(graph, cancellationToken);
        }
    }
}
