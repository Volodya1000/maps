using System.Diagnostics;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Routing;
using CableRoutePlanner.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace CableRoutePlanner.Tests;

public sealed class PreparedRoutingReuseTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RepeatedRealRouteReusesPreparationButOwnsFreshEdgeStatistics()
    {
        RoutingTestLogger logger = new();
        GridRoutePlanner planner = new(new SqliteGeodataRepository(TestData.RealData),
            new GridGenerator(), new AStarGridPathfinder(), logger);
        GeoCoordinate[] points = [new(53.9409, 27.4977), new(53.9424, 27.4991)];
        Stopwatch timer = Stopwatch.StartNew();

        IReadOnlyList<RouteAlternative> cold = await planner.PlanAsync(points, new RoutingOptions(), default);
        long coldMilliseconds = timer.ElapsedMilliseconds;
        timer.Restart();
        IReadOnlyList<RouteAlternative> warm = await planner.PlanAsync(points, new RoutingOptions(), default);
        long warmMilliseconds = timer.ElapsedMilliseconds;

        Assert.Equal(cold.Select(route => route.Geometry), warm.Select(route => route.Geometry));
        Assert.Equal(cold.Select(route => route.Metrics), warm.Select(route => route.Metrics));
        RoutingLogEntry[] preparation = logger.Events.Where(entry => entry.Event.Id == 1).ToArray();
        RoutingLogEntry[] search = logger.Events.Where(entry => entry.Event.Id == 3).ToArray();
        Assert.Equal(2, preparation.Length);
        Assert.Equal(0L, preparation[1].Fields["QueryMilliseconds"]);
        Assert.Equal(0L, preparation[1].Fields["PreparationMilliseconds"]);
        Assert.Equal(2, search.Length);
        Assert.Equal(search[0].Fields["EdgeEvaluations"], search[1].Fields["EdgeEvaluations"]);
        Assert.Equal(446, search[0].Fields["EdgeEvaluations"]);
        output.WriteLine($"real-route: coldMs={coldMilliseconds}, warmMs={warmMilliseconds}, "
            + $"nodes={search[0].Fields["NodeCount"]}, uniqueEdges={search[0].Fields["EdgeEvaluations"]}");
    }
}
