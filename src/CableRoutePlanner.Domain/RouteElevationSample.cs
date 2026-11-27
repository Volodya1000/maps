namespace CableRoutePlanner.Domain;

public sealed record RouteElevationSample(
    GeoCoordinate Coordinate, double DistanceMetres, double? ElevationMetres, double? GradePercent)
{
    public double DistanceMetres { get; } = double.IsFinite(DistanceMetres) && DistanceMetres >= 0
        ? DistanceMetres
        : throw new ArgumentOutOfRangeException(nameof(DistanceMetres), DistanceMetres, "Расстояние должно быть конечным и неотрицательным.");
    public double? ElevationMetres { get; } = ElevationMetres is not double elevation || double.IsFinite(elevation)
        ? ElevationMetres
        : throw new ArgumentOutOfRangeException(nameof(ElevationMetres), ElevationMetres, "Известная высота должна быть конечной.");
    public double? GradePercent { get; } = GradePercent is not double grade || (double.IsFinite(grade) && ElevationMetres.HasValue)
        ? GradePercent
        : throw new ArgumentOutOfRangeException(nameof(GradePercent), GradePercent, "Уклон должен быть конечным и требует известной высоты.");
}
