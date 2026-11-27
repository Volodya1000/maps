using CableRoutePlanner.Domain;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class RouteElevationProfileTests
{
    private static readonly ElevationSource Source = new("Synthetic", "1", "EGM2008", 30, "Test data", new Uri("https://example.com/license"));
    private static readonly GeoCoordinate Start = new(0, 0);
    private static readonly GeoCoordinate Finish = new(0, 0.001);

    [Fact]
    public void SampleListIsCopiedBeforePublication()
    {
        RouteElevationSample[] samples = [new(Start, 0, 50, 30), new(Finish, 100, 80, 30)];
        RouteElevationProfile profile = new(Source, samples, 100, Math.Sqrt(10900), 30, 0, true);

        samples[1] = new(Finish, 100, null, null);

        Assert.Equal(80, profile.Samples[1].ElevationMetres);
        Assert.True(profile.HasCompleteCoverage);
    }

    [Fact]
    public void CompleteProfileRejectsContradictorySurfaceLength() =>
        Assert.Throws<ArgumentOutOfRangeException>("surfaceLengthMetres", () =>
            new RouteElevationProfile(Source, [new(Start, 0, 50, null), new(Finish, 100, 80, null)], 100, 100, 30, 0, true));

    [Fact]
    public void UnknownSampleCannotClaimACompleteAscent() =>
        Assert.Throws<ArgumentOutOfRangeException>("ascentMetres", () =>
            new RouteElevationProfile(Source, [new(Start, 0, 50, null), new(Finish, 100, null, null)], 100, null, 0, null, true));

    [Fact]
    public void StationsMustBeStrictlyIncreasing() =>
        Assert.Throws<ArgumentOutOfRangeException>("samples", () =>
            new RouteElevationProfile(Source, [new(Start, 0, 50, null), new(Finish, 0, 80, null)], 0, 30, 30, 0, true));

    [Fact]
    public void InvalidSampleHeightIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>("ElevationMetres", () =>
            new RouteElevationSample(Start, 0, double.NaN, null));
}
