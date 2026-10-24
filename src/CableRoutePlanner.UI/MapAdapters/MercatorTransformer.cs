using Mapsui.Extensions;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using CableRoutePlanner.Application;

namespace CableRoutePlanner.UI.MapAdapters;

internal sealed class MercatorTransformer : GeometryTransformer
{
    protected override CoordinateSequence TransformCoordinates(CoordinateSequence coords, Geometry parent) =>
        Factory.CoordinateSequenceFactory.Create(
            coords.ToCoordinateArray()
                .Select(c => MapsuiMapAdapter.Project(new GeoCoordinate(c.Y, c.X)))
                .ToArray());
}
