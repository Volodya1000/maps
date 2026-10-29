using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Routing;
using Microsoft.Extensions.Logging.Abstractions;

namespace CableRoutePlanner.Tests;

internal static class CoverageTestData
{
    public static GeoBounds BelarusBounds { get; } = new(23.174801, 51.234408, 32.76947, 56.173846);
    public static GeoCoverage Concave() => new([
        TestData.P(0, 0), TestData.P(350, 0), TestData.P(350, 600), TestData.P(250, 600),
        TestData.P(250, 200), TestData.P(100, 200), TestData.P(100, 600), TestData.P(0, 600), TestData.P(0, 0)]);

    public static MemoryData Repository() => new()
    {
        Info = new MemoryData().Info with { Coverage = Concave() }
    };

    public static IRoutePlanner Planner(IGeodataRepository repository) => new GridRoutePlanner(repository,
        new GridGenerator(), new AStarGridPathfinder(), NullLogger<GridRoutePlanner>.Instance);
}
