namespace CableRoutePlanner.Domain;

public sealed record RouteAlternative(
    Guid Id,
    string Name,
    IReadOnlyList<GeoCoordinate> Geometry,
    RouteMetrics Metrics,
    IReadOnlyList<GeoCoordinate>? TransitionPoints = null,
    IReadOnlyList<RouteRoadContact>? RoadContacts = null,
    IReadOnlyList<long>? GasPipelineCandidateIds = null,
    IReadOnlyList<long>? UnderpassCandidateIds = null,
    IReadOnlyList<RoadCrossingEvent>? RoadCrossingEvents = null,
    IReadOnlyList<UnderpassPassage>? UnderpassPassages = null)
{
    // Allow floating-point round-off when interpolating latitude and wrapped longitude.
    private const double InterpolationToleranceDegrees = 1e-12;

    public RouteElevationProfile? ElevationProfile => Metrics.ElevationProfile;

    public RouteAlternative WithElevationSamples(
        ElevationSource? source, IReadOnlyList<RouteElevationSample> samples, double horizontalLengthMetres)
    {
        RouteElevationProfile profile = RouteElevationProfile.Create(source, samples, horizontalLengthMetres, IsElevationCableLengthApplicable());
        ValidateSampleCoordinates(profile.Samples);
        return this with { Metrics = Metrics.WithElevationProfile(profile) };
    }

    public void ValidateElevationProfile()
    {
        if (ElevationProfile is not { } profile)
        {
            return;
        }
        if (profile.IsCableLengthApplicable != IsElevationCableLengthApplicable())
        {
            throw new ArgumentException($"ElevationProfile.IsCableLengthApplicable={profile.IsCableLengthApplicable}: route {Id} requires {!profile.IsCableLengthApplicable} for its current crossings.");
        }
        ValidateSampleCoordinates(profile.Samples);
    }

    private void ValidateSampleCoordinates(IReadOnlyList<RouteElevationSample> samples)
    {
        if (samples.Count == 0)
        {
            return;
        }
        ArgumentNullException.ThrowIfNull(Geometry);
        if (Geometry.Count == 0 || !SameLocation(samples[0].Coordinate, Geometry[0]))
        {
            throw new ArgumentException($"ElevationProfile.Samples[0].Coordinate={samples[0].Coordinate}: expected the first vertex of route {Id}.");
        }

        int sampleIndex = 1;
        for (int vertex = 1; vertex < Geometry.Count; vertex++)
        {
            GeoCoordinate start = Geometry[vertex - 1];
            GeoCoordinate end = Geometry[vertex];
            if (SameLocation(start, end))
            {
                continue;
            }
            double previousFraction = 0;
            while (sampleIndex < samples.Count && !SameLocation(samples[sampleIndex].Coordinate, end))
            {
                GeoCoordinate coordinate = samples[sampleIndex].Coordinate;
                double fraction = SegmentFraction(start, end, coordinate);
                if (!(fraction > previousFraction && fraction < 1))
                {
                    throw new ArgumentException($"ElevationProfile.Samples[{sampleIndex}].Coordinate={coordinate}: expected the next station on route {Id} between {start} and {end}.");
                }
                previousFraction = fraction;
                sampleIndex++;
            }
            if (sampleIndex == samples.Count)
            {
                throw new ArgumentException($"ElevationProfile.Samples.Count={samples.Count}: route {Id} vertex Geometry[{vertex}]={end} has no station.");
            }
            sampleIndex++;
        }
        if (sampleIndex < samples.Count)
        {
            throw new ArgumentException($"ElevationProfile.Samples[{sampleIndex}].Coordinate={samples[sampleIndex].Coordinate}: route {Id} already ends at {Geometry[^1]}.");
        }
    }

    private static bool SameLocation(GeoCoordinate first, GeoCoordinate second) =>
        first.Latitude == second.Latitude
        && (Math.Abs(first.Latitude) == 90 || LongitudeDelta(first, second) == 0);

    private static double LongitudeDelta(GeoCoordinate start, GeoCoordinate point) =>
        Math.IEEERemainder(point.Longitude - start.Longitude, 360);

    private static double SegmentFraction(GeoCoordinate start, GeoCoordinate end, GeoCoordinate point)
    {
        double latitudeSpan = end.Latitude - start.Latitude;
        double longitudeSpan = LongitudeDelta(start, end);
        double latitudeOffset = point.Latitude - start.Latitude;
        double longitudeOffset = LongitudeDelta(start, point);
        // The larger component gives the more stable fraction for near-axis-aligned segments.
        bool useLatitude = Math.Abs(latitudeSpan) >= Math.Abs(longitudeSpan);
        double fraction = useLatitude ? latitudeOffset / latitudeSpan : longitudeOffset / longitudeSpan;
        double errorDegrees = useLatitude
            ? longitudeOffset - fraction * longitudeSpan
            : latitudeOffset - fraction * latitudeSpan;
        return Math.Abs(errorDegrees) <= InterpolationToleranceDegrees ? fraction : double.NaN;
    }

    private bool IsElevationCableLengthApplicable() =>
        Metrics is { ApprovedWaterCrossings: 0, PotentialBridgeCrossings: 0, PotentialGasPipelineCrossings: 0 }
        && TransitionPoints is not { Count: > 0 }
        && GasPipelineCandidateIds is not { Count: > 0 }
        && UnderpassCandidateIds is not { Count: > 0 }
        && UnderpassPassages is not { Count: > 0 }
        && RoadCrossingEvents is not { Count: > 0 };
}
