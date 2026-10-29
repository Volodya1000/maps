using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure;
using CableRoutePlanner.Persistence;
using System.Text.Json.Nodes;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class CountryCoverageTests
{
    [Theory]
    [InlineData(55.888647, 30.884360)]
    [InlineData(55.891142, 30.770691)]
    [InlineData(55.240084, 31.231595)]
    [InlineData(55.010145, 31.436097)]
    [InlineData(55.533738, 25.497064)]
    [InlineData(55.481000, 25.170434)]
    [InlineData(55.813344, 26.608976)]
    [InlineData(55.834524, 26.811280)]
    public async Task ScreenshotCoordinatesAreOutsideBelarusExtractDespiteBeingInsideItsEnvelope(double latitude, double longitude)
    {
        using var fixture = new SqliteGeodataFixture([], null, regionId: "belarus", bounds: CoverageTestData.BelarusBounds);
        GeoCoordinate point = new(latitude, longitude);
        Assert.True(fixture.Repository.Info.Bounds.Contains(point));
        Assert.False(fixture.Repository.Info.Contains(point));

        var session = new PlanningSession(CoverageTestData.Planner(fixture.Repository), new GeodataPointPlacementValidator(fixture.Repository));
        PlanningException error = await Assert.ThrowsAsync<PlanningException>(() => session.AddAsync(point, default));

        Assert.Equal(PlanningErrorReason.PointOutsidePackage, error.Reason);
        Assert.Empty(session.Points);
        Assert.Equal(0, session.Revision);
    }

    [Theory]
    [InlineData(53.900, 27.550)]
    [InlineData(52.100, 23.700)]
    [InlineData(55.640, 27.060)]
    [InlineData(55.777, 27.937)]
    [InlineData(53.413, 32.590)]
    [InlineData(51.798, 29.500)]
    [InlineData(51.790, 24.080)]
    public async Task RealBorderAreasInsideExtractRemainAvailableEvenWithNoMappedFeatures(double latitude, double longitude)
    {
        using var fixture = new SqliteGeodataFixture([], null, regionId: "belarus", bounds: CoverageTestData.BelarusBounds);
        GeoCoordinate point = new(latitude, longitude);

        Assert.True(fixture.Repository.Info.Contains(point));
        await new GeodataPointPlacementValidator(fixture.Repository).ValidateAsync(point, default);
    }

    [Fact]
    public void InvalidExplicitCoverageCannotSilentlyFallBackToTheRectangle()
    {
        using var fixture = new SqliteGeodataFixture();
        string manifestPath = Path.Combine(fixture.DirectoryPath, "manifest.json");
        JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        foreach (string value in new[] { "{}", "[]", "[[27,54,0]]", "[42]" })
        {
            manifest["coverage_boundary"] = JsonNode.Parse(value);
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            ArgumentException error = Assert.Throws<ArgumentException>(() => new SqliteGeodataRepository(fixture.DirectoryPath));
            Assert.Equal("boundary", error.ParamName);
        }
    }

    [Fact]
    public void ExplicitPackageCoverageTakesPrecedenceAndOtherLegacyPackagesStayRectangular()
    {
        using var explicitPackage = new SqliteGeodataFixture([], null, CoverageTestData.Concave(), regionId: "belarus");
        Assert.False(explicitPackage.Repository.Info.Contains(TestData.P(175, 500)));
        Assert.True(explicitPackage.Repository.Info.Contains(TestData.P(50, 500)));
        using var legacy = new SqliteGeodataFixture();
        Assert.Null(legacy.Repository.Info.Coverage);
        Assert.True(legacy.Repository.Info.Contains(TestData.P(175, 500)));
    }
}
