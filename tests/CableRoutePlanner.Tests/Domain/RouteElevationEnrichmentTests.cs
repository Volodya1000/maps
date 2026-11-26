using CableRoutePlanner.Domain;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class RouteElevationEnrichmentTests
{
    private static readonly GeoCoordinate Start = new(0, 0);
    private static readonly GeoCoordinate Finish = new(0, 0.001);
    private static readonly ElevationSource Source = new("Synthetic", "1", "EGM2008", 30, "Test", new Uri("https://example.com/license"));

    [Fact]
    public void CopyingMetricsPreservesTheCompleteProfileBehindTheCableEstimate()
    {
        RouteAlternative route = CreateRoute();
        RouteAlternative enriched = AddSlope(route);

        RouteAlternative copy = route with { Metrics = enriched.Metrics };

        Assert.True(copy.ElevationProfile?.HasCompleteCoverage);
        Assert.Equal(114.843371598, copy.Metrics.CableLengthMetres(0.1), 7);
    }

    [Fact]
    public void UpdatingLegacyAscentCannotContradictTheAttachedProfile()
    {
        RouteAlternative enriched = AddSlope(CreateRoute());

        RouteAlternative copy = enriched with { Metrics = enriched.Metrics with { AscentMetres = 42 } };

        Assert.Equal(30, copy.ElevationProfile?.AscentMetres);
        Assert.Equal(30, copy.Metrics.AscentMetres);
    }

    [Fact]
    public void ReplacingCompleteElevationWithPartialCoverageRestoresThePlanarCableEstimate()
    {
        RouteAlternative enriched = AddSlope(CreateRoute());

        RouteAlternative updated = enriched.WithElevationSamples(Source,
            [new(Start, 0, 50, null), new(Finish, 100, null, null)], 100);

        Assert.False(updated.ElevationProfile?.HasCompleteCoverage);
        Assert.Null(updated.Metrics.SurfaceLengthMetres);
        Assert.Null(updated.Metrics.AscentMetres);
        Assert.Equal(110, updated.Metrics.CableLengthMetres(0.1), 7);
    }

    private static RouteAlternative CreateRoute() =>
        new(Guid.NewGuid(), "Synthetic", [Start, Finish], new(100, 0, 0, 0, 0, 0, 100, null));

    private static RouteAlternative AddSlope(RouteAlternative route) =>
        route.WithElevationSamples(Source, [new(Start, 0, 50, 30), new(Finish, 100, 80, 30)], 100);
}
