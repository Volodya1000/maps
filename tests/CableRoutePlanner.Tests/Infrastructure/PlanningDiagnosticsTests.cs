using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class PlanningDiagnosticsTests
{
    [Theory]
    [InlineData(-0.1)]
    [InlineData(0)]
    [InlineData(0.1)]
    public async Task LengthLimitHasAnInclusiveBoundaryAndStructuredMeasurements(double extraAllowance)
    {
        GeoCoordinate[] points = [TestData.P(50, 150), TestData.P(260, 150)];
        double actual = points[0].DistanceTo(points[1]);
        RoutingOptions options = TestData.Options with { MaxRouteLengthMetres = actual + extraAllowance };

        if (extraAllowance < 0)
        {
            PlanningException error = await Assert.ThrowsAsync<PlanningException>(() =>
                TestData.Planner().PlanAsync(points, options, default));
            Assert.Equal(PlanningErrorReason.RouteLengthLimitExceeded, error.Reason);
            Assert.Equal(actual, error.ActualRouteLengthMetres!.Value, 6);
            Assert.Equal(options.MaxRouteLengthMetres, error.MaximumRouteLengthMetres);
            Assert.Equal(CostProfile.Defaults.Select(profile => profile.Name), error.SearchFailures.Select(failure => failure.Profile));
            Assert.All(error.SearchFailures, failure => Assert.Equal(PlanningErrorReason.RouteLengthLimitExceeded, failure.Error.Reason));
        }
        else
        {
            IReadOnlyList<RouteAlternative> routes = await TestData.Planner().PlanAsync(points, options, default);
            Assert.NotEmpty(routes);
            Assert.All(routes, route => Assert.True(route.Metrics.LengthMetres <= options.MaxRouteLengthMetres));
        }
    }

    [Fact]
    public async Task ShorterFeasibleRouteSurvivesWhenCheaperClearingDetourExceedsLimit()
    {
        MapFeature clearing = TestData.Polygon(1, TerrainKind.Clearing, 100, 150, 300, 300);
        GeoCoordinate[] points = [TestData.P(50, 350), TestData.P(350, 350)];
        IReadOnlyList<RouteAlternative> unrestricted = await TestData.Planner(clearing).PlanAsync(points, TestData.Options, default);
        RouteAlternative cheapest = unrestricted.MinBy(route => route.Metrics.WeightedCost)!;
        Assert.True(cheapest.Metrics.LengthMetres > 301);
        Assert.True(cheapest.Metrics.WeightedCost < 300);

        IReadOnlyList<RouteAlternative> constrained = await TestData.Planner(clearing).PlanAsync(
            points, TestData.Options with { MaxRouteLengthMetres = 301 }, default);

        Assert.Contains(constrained, route => route.Name == "Короче");
        Assert.All(constrained, route => Assert.True(route.Metrics.LengthMetres <= 301));
    }

    [Theory]
    [InlineData(TerrainKind.Building)]
    [InlineData(TerrainKind.Road)]
    public async Task FailedSearchRetainsEveryProfileWithoutInventingAnObstacleCause(TerrainKind kind)
    {
        MapFeature barrier = kind == TerrainKind.Road
            ? TestData.Line(1, kind, (150, -5000), (150, 5000))
            : TestData.Polygon(1, kind, 140, -5000, 180, 5000);

        PlanningException error = await Assert.ThrowsAsync<PlanningException>(() => TestData.Planner(barrier).PlanAsync(
            [TestData.P(50, 150), TestData.P(300, 150)], TestData.Options, default));

        Assert.Equal(PlanningErrorReason.NoRouteInSearchArea, error.Reason);
        Assert.Null(error.TerrainKind);
        Assert.Equal(3, error.SearchFailures.Count);
        Assert.All(error.SearchFailures, failure => Assert.Equal(PlanningErrorReason.NoRouteInSearchArea, failure.Error.Reason));
        Assert.Same(error.SearchFailures[0].Error, error.InnerException);
    }

    [Fact]
    public async Task UnavailableGridConnectionRemainsDistinctFromAnExhaustedSearch()
    {
        MapFeature building = TestData.Polygon(1, TerrainKind.Building, -100, -100, 500, 800);

        PlanningException error = await Assert.ThrowsAsync<PlanningException>(() => TestData.Planner(building).PlanAsync(
            [TestData.P(50, 150), TestData.P(300, 150)], TestData.Options, default));

        Assert.Equal(PlanningErrorReason.GridConnectionUnavailable, error.Reason);
        Assert.Equal(3, error.SearchFailures.Count);
        Assert.All(error.SearchFailures, failure => Assert.Equal(PlanningErrorReason.GridConnectionUnavailable, failure.Error.Reason));
    }

    [Fact]
    public async Task SearchResourceLimitIsNotReclassifiedAsNoRoute()
    {
        PlanningException error = await Assert.ThrowsAsync<PlanningException>(() => TestData.Planner().PlanAsync(
            [TestData.P(10, 10), TestData.P(300, 600)], new RoutingOptions(5, 100), default));

        Assert.Equal(PlanningErrorReason.ResourceLimit, error.Reason);
    }

    [Theory]
    [InlineData("Нет соединения")]
    [InlineData("No grid connection")]
    public async Task DiagnosticPriorityDependsOnTypedReasonsRatherThanLanguage(string message)
    {
        PlanningException connectionError = new(message, PlanningErrorReason.GridConnectionUnavailable);
        GridRoutePlanner planner = Planner(new ScenarioPathfinder((_, _, profile) =>
        {
            throw profile.Name == "Проще прокладка" ? connectionError
                : new PlanningException("Search exhausted", PlanningErrorReason.NoRouteInSearchArea);
        }));

        PlanningException error = await Assert.ThrowsAsync<PlanningException>(() => planner.PlanAsync(
            [TestData.P(50, 150), TestData.P(300, 150)], TestData.Options, default));

        Assert.Equal(PlanningErrorReason.GridConnectionUnavailable, error.Reason);
        Assert.Same(connectionError, error.InnerException);
        Assert.Equal(3, error.SearchFailures.Count);
        Assert.Same(connectionError, error.SearchFailures[2].Error);
    }

    [Theory]
    [InlineData("Короче")]
    [InlineData("Проще прокладка")]
    public async Task OneFailedProfileDoesNotDiscardAnotherProfilesValidRoute(string failedProfile)
    {
        GridRoutePlanner planner = Planner(new ScenarioPathfinder((start, end, profile) =>
            profile.Name == failedProfile
                ? throw new PlanningException("Unavailable", PlanningErrorReason.GridConnectionUnavailable)
                : [start, new(start.X, start.Y + 120), new(end.X, end.Y + 120), end]));

        IReadOnlyList<RouteAlternative> routes = await planner.PlanAsync(
            [TestData.P(50, 150), TestData.P(300, 150)], TestData.Options, default);

        Assert.NotEmpty(routes);
        Assert.DoesNotContain(routes, route => route.Name == failedProfile);
    }

    [Fact]
    public async Task UnexpectedSearchExceptionPropagates()
    {
        InvalidOperationException failure = new("Programming error");
        GridRoutePlanner planner = Planner(new ScenarioPathfinder((_, _, _) => throw failure));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => planner.PlanAsync(
            [TestData.P(50, 150), TestData.P(300, 150)], TestData.Options, default));

        Assert.Same(failure, error);
    }

    private static GridRoutePlanner Planner(IGridPathfinder pathfinder) => new(
        new MemoryData(TestData.Polygon(1, TerrainKind.Building, 140, 100, 180, 200)),
        new GridGenerator(), pathfinder, NullLogger<GridRoutePlanner>.Instance);

    private sealed class ScenarioPathfinder(Func<Coordinate, Coordinate, CostProfile, IReadOnlyList<Coordinate>> search) : IGridPathfinder
    {
        public IReadOnlyList<Coordinate> FindPath(Coordinate start, Coordinate destination, GridMesh mesh,
            CostProfile profile, Func<Coordinate, Coordinate, double?> evaluate,
            Func<int, int, double?> gridEdgeCost, CancellationToken cancellationToken) => search(start, destination, profile);
    }
}
