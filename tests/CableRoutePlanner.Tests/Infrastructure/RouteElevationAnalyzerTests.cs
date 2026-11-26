using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Elevation;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class RouteElevationAnalyzerTests
{
    // WGS84 equatorial radius, used here to place the synthetic fixture at known stations.
    private const double EquatorialMetresPerDegree = 111319.49079327358;
    private static readonly GeoCoordinate Start = new(0, 0);
    private static readonly GeoCoordinate Finish = new(0, 100 / EquatorialMetresPerDegree);
    private static readonly ElevationSource TestSource = CreateSource(30);

    [Theory]
    [InlineData(0, 100)]
    [InlineData(30, 104.4030650891)]
    public async Task TerrainLengthUsesWgs84HorizontalAndAppliesReserveOnce(double rise, double expected)
    {
        RouteAlternative route = MakeRoute(Start, Finish);
        RouteElevationAnalyzer analyzer = new(new ElevationProvider(TestSource, point => rise * StationFraction(point)));

        RouteAlternative enriched = await analyzer.AnalyzeAsync(route, default);

        RouteElevationProfile profile = Assert.IsType<RouteElevationProfile>(enriched.ElevationProfile);
        Assert.True(profile.HasCompleteCoverage);
        Assert.Equal(100, profile.HorizontalLengthMetres, 7);
        Assert.Equal(expected, Assert.IsType<double>(profile.SurfaceLengthMetres), 7);
        Assert.Equal(rise, Assert.IsType<double>(profile.AscentMetres), 7);
        Assert.Equal(0, profile.DescentMetres);
        Assert.Equal(profile.AscentMetres, enriched.Metrics.AscentMetres);
        Assert.Equal(expected * 1.1, enriched.Metrics.CableLengthMetres(0.1), 7);
        Assert.Equal(route.Metrics.LengthMetres, enriched.Metrics.LengthMetres);
        Assert.Equal(route.Metrics.WeightedCost, enriched.Metrics.WeightedCost);
        Assert.Same(route.Geometry, enriched.Geometry);
    }

    [Fact]
    public async Task InteriorHillIncreasesLengthBetweenEqualEndpoints()
    {
        RouteElevationAnalyzer analyzer = new(new ElevationProvider(TestSource, point =>
            200 * Math.Min(StationFraction(point), 1 - StationFraction(point))));

        RouteAlternative enriched = await analyzer.AnalyzeAsync(MakeRoute(Start, Finish), default);

        RouteElevationProfile profile = Assert.IsType<RouteElevationProfile>(enriched.ElevationProfile);
        Assert.Equal(0, profile.Samples[0].ElevationMetres);
        Assert.Equal(0, profile.Samples[^1].ElevationMetres);
        Assert.InRange(Assert.IsType<double>(profile.SurfaceLengthMetres), 200, 224);
        Assert.InRange(profile.Samples.Max(sample => sample.ElevationMetres!.Value), 80, 100);
        for (int index = 1; index < profile.Samples.Count; index++)
        {
            Assert.InRange(profile.Samples[index].DistanceMetres - profile.Samples[index - 1].DistanceMetres, 0, 15);
        }
    }

    [Fact]
    public async Task ReverseTraversalKeepsLengthAndSwapsAscentAndDescent()
    {
        RouteElevationAnalyzer analyzer = new(new ElevationProvider(TestSource, point => 30 * StationFraction(point)));

        RouteAlternative enriched = await analyzer.AnalyzeAsync(MakeRoute(Finish, Start), default);

        RouteElevationProfile profile = Assert.IsType<RouteElevationProfile>(enriched.ElevationProfile);
        Assert.Equal(104.4030650891, Assert.IsType<double>(profile.SurfaceLengthMetres), 7);
        Assert.Equal(0, profile.AscentMetres);
        Assert.Equal(30, Assert.IsType<double>(profile.DescentMetres), 7);
        Assert.All(profile.Samples, sample => Assert.Equal(-30, Assert.IsType<double>(sample.GradePercent), 7));
    }

    [Fact]
    public async Task IntermediateVerticesRemainInProfileAndWgs84LengthFollowsTheBend()
    {
        GeoCoordinate bend = new(0, 0.001);
        GeoCoordinate end = new(0.001, 0.001);
        RouteElevationAnalyzer analyzer = new(new ElevationProvider(TestSource, _ => 42));

        RouteAlternative enriched = await analyzer.AnalyzeAsync(MakeRoute(Start, bend, end), default);

        RouteElevationProfile profile = Assert.IsType<RouteElevationProfile>(enriched.ElevationProfile);
        Assert.Contains(profile.Samples, sample => sample.Coordinate == bend);
        // Equatorial arc of 0.001° plus meridian arc of 0.001° at the equator.
        Assert.Equal(221.893766615, profile.HorizontalLengthMetres, 6);
    }

    [Fact]
    public async Task NoDataDoesNotBridgeGapOrProduceWholeRouteTotals()
    {
        RouteElevationAnalyzer analyzer = new(new ElevationProvider(TestSource, point =>
            StationFraction(point) switch { < 0.4 => 10, > 0.6 => 90, _ => null }));

        RouteAlternative enriched = await analyzer.AnalyzeAsync(MakeRoute(Start, Finish), default);

        RouteElevationProfile profile = Assert.IsType<RouteElevationProfile>(enriched.ElevationProfile);
        Assert.False(profile.HasCompleteCoverage);
        Assert.Contains(profile.Samples, sample => sample.ElevationMetres is null && sample.GradePercent is null);
        Assert.All(profile.Samples.Where(sample => sample.ElevationMetres.HasValue), sample => Assert.Equal(0, sample.GradePercent));
        Assert.Null(profile.SurfaceLengthMetres);
        Assert.Null(profile.AscentMetres);
        Assert.Null(profile.DescentMetres);
        Assert.Null(enriched.Metrics.SurfaceLengthMetres);
        Assert.Equal(110, enriched.Metrics.CableLengthMetres(0.1), 7);
    }

    [Theory]
    [InlineData(1, 0, 0, false)]
    [InlineData(0, 1, 0, false)]
    [InlineData(0, 0, 1, false)]
    [InlineData(0, 0, 0, true)]
    public async Task StructuralCrossingKeepsSurfaceProfileButUsesLegacyCableBase(
        int water, int bridges, int gas, bool underpass)
    {
        RouteAlternative route = MakeRoute(Start, Finish);
        route = route with
        {
            Metrics = route.Metrics with
            {
                ApprovedWaterCrossings = water,
                PotentialBridgeCrossings = bridges,
                PotentialGasPipelineCrossings = gas
            },
            UnderpassCandidateIds = underpass ? [1] : []
        };
        RouteElevationAnalyzer analyzer = new(new ElevationProvider(TestSource, point => 30 * StationFraction(point)));

        RouteAlternative enriched = await analyzer.AnalyzeAsync(route, default);

        RouteElevationProfile profile = Assert.IsType<RouteElevationProfile>(enriched.ElevationProfile);
        Assert.True(profile.HasCompleteCoverage);
        Assert.False(profile.IsCableLengthApplicable);
        Assert.Equal(104.4030650891, Assert.IsType<double>(profile.SurfaceLengthMetres), 7);
        Assert.Null(enriched.Metrics.SurfaceLengthMetres);
        Assert.Equal(110, enriched.Metrics.CableLengthMetres(0.1), 7);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableSamplingPreservesHorizontalDistanceAndLegacyCableEstimate(bool samplingBudgetExceeded)
    {
        ElevationSource? source = samplingBudgetExceeded ? CreateSource(0.0001) : null;
        RouteElevationAnalyzer analyzer = new(new ElevationProvider(source, _ => 42));

        RouteAlternative enriched = await analyzer.AnalyzeAsync(MakeRoute(Start, Finish), default);

        RouteElevationProfile profile = Assert.IsType<RouteElevationProfile>(enriched.ElevationProfile);
        Assert.Equal(source, profile.Source);
        Assert.Equal(100, profile.HorizontalLengthMetres, 7);
        Assert.False(profile.HasCompleteCoverage);
        Assert.Null(profile.SurfaceLengthMetres);
        Assert.Equal(110, enriched.Metrics.CableLengthMetres(0.1), 7);
    }

    [Fact]
    public async Task CancelledDemReadCannotReturnAnEnrichedRoute()
    {
        using CancellationTokenSource cancellation = new();
        RouteElevationAnalyzer analyzer = new(new ElevationProvider(TestSource, _ =>
        {
            cancellation.Cancel();
            return 42;
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => analyzer.AnalyzeAsync(MakeRoute(Start, Finish), cancellation.Token));
    }

    private static ElevationSource CreateSource(double resolutionMetres) =>
        new("Synthetic", "1", "EGM2008", resolutionMetres, "Test data", new Uri("https://example.com/license"));

    private static double StationFraction(GeoCoordinate point) => point.Longitude / Finish.Longitude;

    private static RouteAlternative MakeRoute(params GeoCoordinate[] coordinates) =>
        new(Guid.NewGuid(), "Test", coordinates, new(100, 0, 0, 0, 0, 0, 102, null));

    private sealed class ElevationProvider(ElevationSource? source, Func<GeoCoordinate, double?> elevation) : IElevationProvider
    {
        public ElevationSource? Source { get; } = source;

        public Task<IReadOnlyList<double?>> ReadAsync(IReadOnlyList<GeoCoordinate> points, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<double?>>(points.Select(elevation).ToArray());
    }
}
