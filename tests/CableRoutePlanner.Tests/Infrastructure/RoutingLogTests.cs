using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Routing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class RoutingLogTests
{
    [Fact]
    public async Task CompletedCalculationKeepsEventNamesLevelsAndStructuredMeasurements()
    {
        RoutingTestLogger logger = new();
        GridRoutePlanner planner = new(new MemoryData(), new GridGenerator(), new AStarGridPathfinder(), logger);
        IReadOnlyList<RouteAlternative> routes = await planner.PlanAsync(
            [TestData.P(50, 150), TestData.P(260, 150)], TestData.Options, default);

        Assert.Equal(3, logger.Events.Count);
        RoutingLogEntry preparation = Assert.Single(logger.Events, entry => entry.Event.Id == 1);
        Assert.Equal("PreparationLog", preparation.Event.Name);
        Assert.IsType<long>(preparation.Fields["QueryMilliseconds"]);
        Assert.IsType<long>(preparation.Fields["PreparationMilliseconds"]);
        Assert.Equal("Routing SQLite query {QueryMilliseconds} ms; WKB, projection and spatial index {PreparationMilliseconds} ms (zero on cache reuse)", preparation.Fields["{OriginalFormat}"]);
        RoutingLogEntry completed = Assert.Single(logger.Events, entry => entry.Event.Id == 2);
        Assert.Equal("RoutesCompletedLog", completed.Event.Name);
        Assert.Equal(routes.Count, completed.Fields["RouteCount"]);
        Assert.Equal(2, completed.Fields["WaypointCount"]);
        Assert.IsType<long>(completed.Fields["ElapsedMilliseconds"]);
        RoutingLogEntry search = Assert.Single(logger.Events, entry => entry.Event.Id == 3);
        Assert.Equal("SearchCompletedLog", search.Event.Name);
        Assert.Equal(routes.Count, search.Fields["RouteCount"]);
        Assert.True(Assert.IsType<int>(search.Fields["NodeCount"]) > 0);
        Assert.True(Assert.IsType<int>(search.Fields["EdgeEvaluations"]) > 0);
        Assert.All(logger.Events, entry =>
        {
            Assert.Equal(LogLevel.Information, entry.Level);
            Assert.Null(entry.Error);
            Assert.DoesNotContain(entry.Fields.Keys, key => key.Contains("Latitude", StringComparison.Ordinal)
                || key.Contains("Longitude", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task FailedProfilesKeepTheirTypedReasonsAndOriginalExceptions()
    {
        RoutingTestLogger logger = new();
        GridRoutePlanner planner = new(new MemoryData(), new GridGenerator(), new AStarGridPathfinder(), logger);
        PlanningException error = await Assert.ThrowsAsync<PlanningException>(() => planner.PlanAsync(
            [TestData.P(50, 150), TestData.P(260, 150)], TestData.Options with { MaxRouteLengthMetres = 100 }, default));

        Assert.Equal(CostProfile.Defaults.Count, error.SearchFailures.Count);
        foreach (ProfileSearchFailure failure in error.SearchFailures)
        {
            RoutingLogEntry entry = Assert.Single(logger.Events, entry => entry.Event.Id == 4
                && Equals(entry.Fields["Profile"], failure.Profile));
            Assert.Equal("ProfileUnavailableLog", entry.Event.Name);
            Assert.Equal(LogLevel.Information, entry.Level);
            Assert.Equal(failure.Error.Reason, entry.Fields["Reason"]);
            Assert.Same(failure.Error, entry.Error);
            Assert.Equal("Profile {Profile} is unavailable ({Reason})", entry.Fields["{OriginalFormat}"]);
        }
        Assert.DoesNotContain(logger.Events, entry => entry.Event.Id is 2 or 3);
    }
}
