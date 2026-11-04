using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using CableRoutePlanner.Infrastructure.Routing;
using NetTopologySuite.Geometries;
using Xunit;
using Xunit.Abstractions;

namespace CableRoutePlanner.Tests;

/// <summary>Fixed-area phase probes use the existing xUnit measurement pattern, without timing pass/fail thresholds.</summary>
public sealed class SuccessfulGraphPhaseTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("empty")]
    [InlineData("building")]
    [InlineData("curved-bridge")]
    [InlineData("waypoints")]
    [InlineData("terrain-profiles")]
    [InlineData("gas-on")]
    [InlineData("minsk-bridge")]
    public async Task FixedAreaGraphsFindValidPathsForEveryProfileAndRecordSeparatePhases(string scenario)
    {
        string productPath = typeof(GridRoutePlanner).Assembly.Location;
        using FileStream product = File.OpenRead(productPath);
        string digest = Convert.ToHexString(SHA256.HashData(product));
        string? expectedDigest = Environment.GetEnvironmentVariable("MAPS_EXPECT_INFRA_SHA256");
        if (expectedDigest is not null)
        { Assert.Equal(expectedDigest, digest); }
        RoutingControlCase input = RoutingControlCase.Create(scenario);
        GeoBounds bounds = input.Data.Info.Bounds;
        Stopwatch timer = Stopwatch.StartNew();
        IReadOnlyList<MapFeature> features = await input.Data.QueryAsync(bounds, default);
        double queryMilliseconds = timer.Elapsed.TotalMilliseconds;
        LocalProjection projection = new(new((bounds.South + bounds.North) / 2, (bounds.West + bounds.East) / 2));
        GeometryFactory factory = new();
        Geometry? coverage = input.Data.Info.Coverage is { } outline
            ? factory.CreatePolygon(outline.Boundary.Select(projection.Forward).ToArray()) : null;
        timer.Restart();
        PreparedTerrainData terrain = new(features, projection, coverage);
        double preparationMilliseconds = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        GridMesh mesh = new GridGenerator().Generate(bounds, input.Options, projection);
        Envelope loaded = new(projection.Forward(new GeoCoordinate(bounds.South, bounds.West)), projection.Forward(new GeoCoordinate(bounds.North, bounds.East)));
        mesh = mesh with { Portals = terrain.TransitionPortals.Where(portal => portal.Coordinates.All(loaded.Covers)).ToArray() };
        double meshMilliseconds = timer.Elapsed.TotalMilliseconds;
        TerrainEvaluator evaluator = new(terrain, preferGas: input.Options.PreferOverheadGasCrossings);
        GridEdgeEvaluationCache edges = new(mesh, evaluator);
        double graphMilliseconds = 0, searchMilliseconds = 0;
        int expandedNodes = 0, successfulLegs = 0;
        foreach (CostProfile profile in CostProfile.Defaults)
        {
            for (int leg = 1; leg < input.Points.Length; leg++)
            {
                Coordinate start = projection.Forward(input.Points[leg - 1]);
                Coordinate destination = projection.Forward(input.Points[leg]);
                if (start.Equals2D(destination))
                { continue; }
                timer.Restart();
                GridSearchGraph graph = new(start, destination, mesh, profile, evaluator, edges, default);
                graphMilliseconds += timer.Elapsed.TotalMilliseconds;
                ExpansionCountingGraph measured = new(graph);
                timer.Restart();
                IReadOnlyList<int> path = Assert.IsAssignableFrom<IReadOnlyList<int>>(new AStarGridPathfinder().FindPath(measured, default));
                searchMilliseconds += timer.Elapsed.TotalMilliseconds;
                expandedNodes += measured.ExpandedNodes;
                successfulLegs++;
                Assert.Equal(graph.StartNodeId, path[0]);
                Assert.Equal(graph.DestinationNodeId, path[^1]);
                Coordinate[] coordinates = path.Select(graph.GetCoordinate).ToArray();
                Assert.True(factory.CreateLineString(coordinates).IsSimple);
                Assert.All(coordinates, coordinate => Assert.True(loaded.Covers(coordinate)));
                for (int index = 1; index < path.Count; index++)
                {
                    EdgeEvaluation edge = Assert.IsType<EdgeEvaluation>(evaluator.EvaluateForSearch(coordinates[index - 1], coordinates[index]));
                    Assert.Equal(edge.Cost(profile), Assert.Single(graph.Neighbours(path[index - 1]), next => next.Id == path[index]).Cost, 6);
                }
            }
        }
        Assert.True(successfulLegs >= CostProfile.Defaults.Count);
        Assert.True(expandedNodes > 0);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"graph-probe: scenario={scenario} backendSha256={digest} queryMs={queryMilliseconds:F3} prepMs={preparationMilliseconds:F3} "
            + $"meshMs={meshMilliseconds:F3} graphMs={graphMilliseconds:F3} aStarMs={searchMilliseconds:F3} "
            + $"expandedNodes={expandedNodes} uniqueGridEdges={edges.EvaluationCount} nodes={mesh.Nodes.Length} successfulLegs={successfulLegs}"));
    }

    private sealed class ExpansionCountingGraph(IRoutingGraph graph) : IRoutingGraph
    {
        public int ExpandedNodes { get; private set; }
        public int NodeCount => graph.NodeCount;
        public int StartNodeId => graph.StartNodeId;
        public int DestinationNodeId => graph.DestinationNodeId;
        public double EstimateRemainingCost(int nodeId) => graph.EstimateRemainingCost(nodeId);
        public IEnumerable<(int Id, double Cost)> Neighbours(int nodeId)
        {
            ExpandedNodes++;
            return graph.Neighbours(nodeId);
        }
    }
}
