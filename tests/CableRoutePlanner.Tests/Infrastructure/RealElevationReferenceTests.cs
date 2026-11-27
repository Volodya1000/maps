using System.Text.Json;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Elevation;
using CableRoutePlanner.Persistence;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class RealElevationReferenceTests
{
    [Fact]
    public async Task DzerzhinskayaNativeDemMatchesIndependentWgs84SurfaceReference()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Elevation");
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(directory, "summit-reference.json")));
        JsonElement reference = document.RootElement.GetProperty("baseline");
        JsonElement expectedSamples = reference.GetProperty("samples");
        JsonElement first = expectedSamples[0];
        JsonElement last = expectedSamples[expectedSamples.GetArrayLength() - 1];
        GeoCoordinate start = Coordinate(first);
        GeoCoordinate finish = Coordinate(last);
        RouteAlternative route = new(Guid.NewGuid(), "Дзержинская — южный склон",
            [start, finish], new(start.DistanceTo(finish), 0, 0, 0, 0, 0, 0, null));
        SqliteElevationProvider provider = new(directory);
        RouteElevationAnalyzer analyzer = new(provider);

        RouteAlternative measured = await analyzer.AnalyzeAsync(route, default);

        RouteElevationProfile profile = Assert.IsType<RouteElevationProfile>(measured.ElevationProfile);
        Assert.True(profile.HasCompleteCoverage);
        Assert.True(profile.IsCableLengthApplicable);
        Assert.Equal("EGM2008", profile.Source!.VerticalDatum);
        Assert.Equal(30, profile.Source.ResolutionMetres);
        Assert.Equal(reference.GetProperty("horizontal_metres").GetDouble(), profile.HorizontalLengthMetres, 6);
        Assert.Equal(reference.GetProperty("surface_metres").GetDouble(),
            Assert.IsType<double>(profile.SurfaceLengthMetres), 6);
        Assert.Equal(reference.GetProperty("ascent_metres").GetDouble(),
            Assert.IsType<double>(profile.AscentMetres), 6);
        Assert.Equal(reference.GetProperty("descent_metres").GetDouble(),
            Assert.IsType<double>(profile.DescentMetres), 6);
        Assert.Equal(reference.GetProperty("cable_with_ten_percent_reserve_metres").GetDouble(),
            measured.Metrics.CableLengthMetres(0.1), 6);
        Assert.Equal(expectedSamples.GetArrayLength(), profile.Samples.Count);
        for (int index = 0; index < profile.Samples.Count; index++)
        {
            Assert.Equal(expectedSamples[index].GetProperty("height_metres").GetDouble(),
                Assert.IsType<double>(profile.Samples[index].ElevationMetres), 6);
        }
        Assert.True(profile.Samples[^1].ElevationMetres < profile.Samples[0].ElevationMetres);
        Assert.Equal(route.Metrics.LengthMetres, measured.Metrics.LengthMetres);
    }

    private static GeoCoordinate Coordinate(JsonElement sample) =>
        new(sample.GetProperty("latitude").GetDouble(), sample.GetProperty("longitude").GetDouble());
}
