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
        var width = maximum.X - minimum.X;
        var height = maximum.Y - minimum.Y;
        var requestedNodes = Math.Max(1d, width / options.CellSizeMetres)
            * Math.Max(1d, height / options.CellSizeMetres);
        if (options.MaxNodes < 1000 && requestedNodes > options.MaxNodes)
        {
            throw new PlanningException(
                "Слишком большая область расчёта. Увеличьте размер ячейки или лимит узлов.");
        }

        // Long inter-city routes use a coarse overview graph. The final path is
        // still validated segment by segment by TerrainEvaluator; the coarse
        // graph only controls where A* spends its time.
        var cellSize = Math.Max(options.CellSizeMetres,
            Math.Sqrt(width * height / options.MaxNodes));
        cellSize = Math.Min(cellSize, 2_000);
        var columns = (int)Math.Floor(width / cellSize) + 1;
        var rows = (int)Math.Floor(height / cellSize) + 1;
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
                    minimum.X + x * cellSize,
                    minimum.Y + y * cellSize);
            }
        }

        return new(nodes, columns, rows, minimum, cellSize);
    }
}
