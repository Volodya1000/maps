namespace CableRoutePlanner.Domain;

public sealed record RouteMetrics(
    double LengthMetres,
    double ForestMetres,
    double FieldMetres,
    int RoadCrossings,
    int ApprovedWaterCrossings,
    int PotentialBridgeCrossings,
    double WeightedCost,
    double? AscentMetres,
    int PotentialGasPipelineCrossings = 0,
    int RailwayCrossings = 0)
{
    private readonly double? _legacyAscentMetres = AscentMetres;

    internal RouteElevationProfile? ElevationProfile { get; private init; }

    public double? AscentMetres
    {
        get => ElevationProfile is null ? _legacyAscentMetres : ElevationProfile.AscentMetres;
        init => _legacyAscentMetres = value;
    }

    public double? SurfaceLengthMetres =>
        ElevationProfile is { IsCableLengthApplicable: true } profile ? profile.SurfaceLengthMetres : null;

    internal RouteMetrics WithElevationProfile(RouteElevationProfile profile) =>
        this with { ElevationProfile = profile };

    public double CableLengthMetres(double reserveFraction)
    {
        if (!double.IsFinite(reserveFraction) || reserveFraction is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(reserveFraction));
        }

        return (SurfaceLengthMetres ?? LengthMetres) * (1 + reserveFraction);
    }
}
