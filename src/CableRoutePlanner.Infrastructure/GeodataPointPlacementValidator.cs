using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace CableRoutePlanner.Infrastructure;

public sealed class GeodataPointPlacementValidator(IGeodataRepository geodata) : IPointPlacementValidator
{
    // One metre models map-click and source-coordinate uncertainty for rivers stored as lines.
    // It is not an estimate of the river's physical width. Polygonal rivers use exact covers.
    private const double RiverLineUncertaintyMetres = 1;
    private readonly GeometryFactory _geometryFactory = new();

    public async Task ValidateAsync(GeoCoordinate coordinate, CancellationToken cancellationToken)
    {
        var coverage = geodata.Info.Bounds;
        if (!coverage.Contains(coordinate))
        {
            throw new PlanningException("Для этой точки нет данных в офлайн-пакете.");
        }

        var queryBounds = QueryBounds(coordinate, coverage);
        var features = await geodata.QueryAsync(queryBounds, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var point = _geometryFactory.CreatePoint(new Coordinate(coordinate.Longitude, coordinate.Latitude));
        var projection = new LocalProjection(coordinate);
        var reader = new WKBReader();
        foreach (var feature in features)
        {
            if (TerrainTraversalPolicy.For(feature.Kind) != TerrainTraversalRule.Blocked)
            {
                continue;
            }

            var geometry = reader.Read(feature.Wkb);
            var blocked = geometry.Covers(point);
            if (!blocked && feature.Kind == TerrainKind.River && geometry.Dimension == Dimension.Curve)
            {
                blocked = projection.Forward(geometry).Distance(_geometryFactory.CreatePoint(new Coordinate(0, 0)))
                    <= RiverLineUncertaintyMetres;
            }

            if (blocked)
            {
                throw new PlanningException($"Нельзя установить точку внутри объекта типа «{TerrainName(feature.Kind)}».");
            }
        }
    }

    private static GeoBounds QueryBounds(GeoCoordinate coordinate, GeoBounds coverage)
    {
        var latitudeMargin = RiverLineUncertaintyMetres / (GeoCoordinate.MeanEarthRadiusMetres * Math.PI / 180);
        var longitudeMargin = latitudeMargin / Math.Cos(coordinate.Latitude * Math.PI / 180);
        var west = Math.Max(coverage.West, coordinate.Longitude - longitudeMargin);
        var east = Math.Min(coverage.East, coordinate.Longitude + longitudeMargin);
        var south = Math.Max(coverage.South, coordinate.Latitude - latitudeMargin);
        var north = Math.Min(coverage.North, coordinate.Latitude + latitudeMargin);
        if (west == east)
        {
            west = Math.Max(coverage.West, west - longitudeMargin);
            east = Math.Min(coverage.East, east + longitudeMargin);
        }

        if (south == north)
        {
            south = Math.Max(coverage.South, south - latitudeMargin);
            north = Math.Min(coverage.North, north + latitudeMargin);
        }

        return new(west, south, east, north);
    }

    private static string TerrainName(TerrainKind terrain) => terrain switch
    {
        TerrainKind.Building => "здания",
        TerrainKind.Lake => "озера",
        TerrainKind.Marsh => "болота",
        TerrainKind.River => "русла реки",
        _ => "запрещённой местности"
    };
}
