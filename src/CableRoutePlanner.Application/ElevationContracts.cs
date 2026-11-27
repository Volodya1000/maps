using CableRoutePlanner.Domain;

namespace CableRoutePlanner.Application;

public interface IElevationProvider
{
    ElevationSource? Source { get; }

    Task<IReadOnlyList<double?>> ReadAsync(IReadOnlyList<GeoCoordinate> points, CancellationToken cancellationToken);
}

public interface IRouteElevationAnalyzer
{
    Task<RouteAlternative> AnalyzeAsync(RouteAlternative route, CancellationToken cancellationToken);
}
