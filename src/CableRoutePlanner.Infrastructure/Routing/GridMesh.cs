using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using NetTopologySuite.Geometries;

namespace CableRoutePlanner.Infrastructure.Routing;

public sealed record GridMesh(
    Coordinate[] Nodes,
    int Columns,
    int Rows,
    Coordinate Minimum,
    double CellSizeMetres);

public interface IGridGenerator
{
    GridMesh Generate(GeoBounds bounds, RoutingOptions options, LocalProjection projection);
}

public sealed class GridGenerator : IGridGenerator
{
    public GridMesh Generate(GeoBounds bounds, RoutingOptions options, LocalProjection projection)
    {
        var minimum = projection.Forward(new GeoCoordinate(bounds.South, bounds.West));
        var maximum = projection.Forward(new GeoCoordinate(bounds.North, bounds.East));
        if (maximum.X - minimum.X > 20_000 || maximum.Y - minimum.Y > 20_000)
        {
            throw new PlanningException(
                "Расчётная область превышает 20 км. Разбейте трассу на отдельные проекты.");
        }

        var columns = (int)Math.Floor((maximum.X - minimum.X) / options.CellSizeMetres) + 1;
        var rows = (int)Math.Floor((maximum.Y - minimum.Y) / options.CellSizeMetres) + 1;
        if ((long)columns * rows > options.MaxNodes)
        {
            throw new PlanningException(
                "Слишком большая область расчёта. Увеличьте размер ячейки или уменьшите область.");
        }

        var nodes = new Coordinate[columns * rows];
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < columns; x++)
            {
                nodes[y * columns + x] = new(
                    minimum.X + x * options.CellSizeMetres,
                    minimum.Y + y * options.CellSizeMetres);
            }
        }

        return new(nodes, columns, rows, minimum, options.CellSizeMetres);
    }
}
