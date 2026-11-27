using System.Text.Json;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using CableRoutePlanner.Infrastructure.Routing;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class SharedContactContractTests
{
    [Fact]
    public async Task SharedUnderpassTopologyExamplesAgreeWithPlannerAndCrossingEvents()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Underpasses", "audit-contract.json")));
        foreach (JsonElement example in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            List<MapFeature> features = [TestData.Line(1, TerrainKind.Road, Points(example.GetProperty("road")))];
            foreach (JsonElement candidate in example.GetProperty("candidates").EnumerateArray())
            {
                MapFeature feature = TestData.Line(candidate.GetProperty("id").GetInt64(),
                    Enum.Parse<TerrainKind>(candidate.GetProperty("kind").GetString()!),
                    Points(candidate.GetProperty("coordinates")));
                if (candidate.TryGetProperty("underpassRoadIds", out JsonElement roads))
                {
                    feature = feature with { UnderpassRoadIds = Ids(roads) };
                }
                if (candidate.TryGetProperty("underpassTopology", out JsonElement topology))
                {
                    feature = feature with
                    {
                        UnderpassTopology = new(topology.GetProperty("networkId").GetInt64(),
                        Ids(topology.GetProperty("nodeIds")), Ids(topology.GetProperty("entranceNodeIds")))
                    };
                }
                features.Add(feature);
            }
            IRoutePlanner planner = TestData.Planner(features.ToArray());
            (double X, double Y)[] route = Points(example.GetProperty("route"));
            GeoCoordinate[] points = [TestData.P(route[0].X, route[0].Y), TestData.P(route[^1].X, route[^1].Y)];
            RoutingOptions options = TestData.Options with { AllowExperimentalPedestrianUnderpasses = true };
            if (!example.GetProperty("runtimeTraversable").GetBoolean())
            {
                PlanningException error = await Assert.ThrowsAsync<PlanningException>(() => planner.PlanAsync(points, options, default));
                Assert.Equal(PlanningErrorReason.NoRouteInSearchArea, error.Reason);
                continue;
            }
            RouteAlternative result = Assert.Single(await planner.PlanAsync(points, options, default));
            string[] kinds = result.RoadCrossingEvents!.Select(crossing => crossing.Kind switch
            {
                RoadCrossingKind.OverheadGasPipeline => "overhead_gas",
                RoadCrossingKind.Bridge => "bridge",
                RoadCrossingKind.PedestrianUnderpass => "pedestrian_underpass",
                _ => throw new InvalidOperationException()
            }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(example.GetProperty("crossingTypesUsed").EnumerateArray().Select(kind => kind.GetString()), kinds);
            if (kinds.Contains("pedestrian_underpass", StringComparer.Ordinal))
            {
                Assert.NotEmpty(result.UnderpassCandidateIds!);
            }
            else
            {
                Assert.Empty(result.UnderpassCandidateIds!);
            }
        }
    }

    private static long[] Ids(JsonElement identifiers) => identifiers.EnumerateArray().Select(id => id.GetInt64()).ToArray();

    [Fact]
    public void SharedContactExamplesAgreeWithRuntimeTraversal()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "routing-contact-contract.json")));
        Assert.Equal("local-metres", document.RootElement.GetProperty("coordinateSystem").GetString());
        long roadId = document.RootElement.GetProperty("roadId").GetInt64();
        int count = 0;
        foreach (JsonElement example in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            List<MapFeature> features = [TestData.Line(roadId, TerrainKind.Road, Points(example.GetProperty("road")))];
            if (example.TryGetProperty("candidate", out JsonElement candidate))
            {
                MapFeature feature = TestData.Line(candidate.GetProperty("id").GetInt64(),
                    Enum.Parse<TerrainKind>(candidate.GetProperty("kind").GetString()!),
                    Points(candidate.GetProperty("coordinates")));
                if (candidate.TryGetProperty("underpassRoadIds", out JsonElement roads))
                {
                    feature = feature with { UnderpassRoadIds = roads.EnumerateArray().Select(id => id.GetInt64()).ToArray() };
                }
                features.Add(feature);
            }
            LocalProjection projection = new(TestData.P(0, 0));
            TerrainEvaluator evaluator = new(features, projection,
                allowUnderpasses: example.GetProperty("allowUnderpasses").GetBoolean());
            (double X, double Y)[] route = Points(example.GetProperty("route"));
            bool traversable = route.Zip(route.Skip(1), (from, to) => evaluator.Evaluate(
                projection.Forward(TestData.P(from.X, from.Y)),
                projection.Forward(TestData.P(to.X, to.Y))) is not null).All(value => value);
            bool valid = example.GetProperty("runtimeTraversable").GetBoolean();
            Assert.True(valid == traversable, example.GetProperty("id").GetString());
            count++;
        }
        Assert.True(count >= 12);
    }

    private static (double X, double Y)[] Points(JsonElement points) => points.EnumerateArray()
        .Select(point => (point[0].GetDouble(), point[1].GetDouble())).ToArray();
}
