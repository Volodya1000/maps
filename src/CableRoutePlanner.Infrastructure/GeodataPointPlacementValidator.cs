using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace CableRoutePlanner.Infrastructure;

public sealed class GeodataPointPlacementValidator(IGeodataRepository geodata) : IPointPlacementValidator
{
    private const double QueryEnvelopeMarginDegrees = 0.000001;
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
        var point = _geometryFactory.CreatePoint(new Coordinate(coordinate.Longitude, coordinate.Latitude));
        var reader = new WKBReader();
        foreach (var feature in features)
        {
            if (TerrainTraversalPolicy.For(feature.Kind) != TerrainTraversalRule.Blocked)
            {
                continue;
            }

            var geometry = reader.Read(feature.Wkb);
            if (geometry.Covers(point))
            {
                throw new PlanningException($"Нельзя установить точку внутри объекта типа «{TerrainName(feature.Kind)}».");
            }
        }
    }

    private static GeoBounds QueryBounds(GeoCoordinate coordinate, GeoBounds coverage)
    {
        var west = Math.Max(coverage.West, coordinate.Longitude - QueryEnvelopeMarginDegrees);
        var east = Math.Min(coverage.East, coordinate.Longitude + QueryEnvelopeMarginDegrees);
        var south = Math.Max(coverage.South, coordinate.Latitude - QueryEnvelopeMarginDegrees);
        var north = Math.Min(coverage.North, coordinate.Latitude + QueryEnvelopeMarginDegrees);
        if (west == east)
        {
            west = Math.Max(coverage.West, west - QueryEnvelopeMarginDegrees);
            east = Math.Min(coverage.East, east + QueryEnvelopeMarginDegrees);
        }

        if (south == north)
        {
            south = Math.Max(coverage.South, south - QueryEnvelopeMarginDegrees);
            north = Math.Min(coverage.North, north + QueryEnvelopeMarginDegrees);
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
