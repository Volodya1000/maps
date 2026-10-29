using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using NetTopologySuite.Geometries;

namespace CableRoutePlanner.Infrastructure.Routing;

public sealed record GridPortal(Coordinate[] Coordinates);

public sealed record GridMesh(
    Coordinate[] Nodes,
    int Columns,
    int Rows,
    Coordinate Minimum,
    double CellSizeMetres,
    IReadOnlyList<GridPortal>? Portals = null);

public interface IGridGenerator
{
    GridMesh Generate(GeoBounds bounds, RoutingOptions options, LocalProjection projection);
}

public sealed class GridGenerator : IGridGenerator
{
    // Tiny meshes cannot represent a long route after coarsening; reject instead of
    // producing a misleading sparse path. Keep the historical threshold for compatibility.
    private const int MinimumNodesForAdaptiveCoarsening = 1000;

    // Upper bound for the overview graph; wider cells risk losing narrow passages.
    private const double MaximumAdaptiveCellSizeMetres = 2_000;

    private const int CellSizeSearchIterations = 48;

    public GridMesh Generate(GeoBounds bounds, RoutingOptions options, LocalProjection projection)
    {
        Coordinate minimum = projection.Forward(new GeoCoordinate(bounds.South, bounds.West));
        Coordinate maximum = projection.Forward(new GeoCoordinate(bounds.North, bounds.East));
        double width = maximum.X - minimum.X;
        double height = maximum.Y - minimum.Y;
        long NodeCount(double size) => ((long)Math.Floor(width / size) + 1)
            * ((long)Math.Floor(height / size) + 1);
        long requestedNodes = NodeCount(options.CellSizeMetres);
        if (options.MaxNodes < MinimumNodesForAdaptiveCoarsening && requestedNodes > options.MaxNodes)
        {
            throw new PlanningException(
                "Слишком большая область расчёта. Увеличьте размер ячейки или лимит узлов.", PlanningErrorReason.ResourceLimit);
        }

        // Long inter-city routes use a coarse overview graph. The final path is
        // still validated segment by segment by TerrainEvaluator; the coarse
        // graph only controls where A* spends its time.
        double cellSize = Math.Max(options.CellSizeMetres,
            Math.Sqrt(width * height / options.MaxNodes));
        cellSize = Math.Min(cellSize, MaximumAdaptiveCellSizeMetres);
        if (NodeCount(cellSize) > options.MaxNodes && NodeCount(MaximumAdaptiveCellSizeMetres) <= options.MaxNodes)
        {
            // A sqrt(area / budget) estimate omits the final row and column.
            // Keep a valid upper bound while finding the smallest usable cell size.
            double lower = cellSize;
            double upper = MaximumAdaptiveCellSizeMetres;
            for (int iteration = 0; iteration < CellSizeSearchIterations; iteration++)
            {
                double middle = lower + (upper - lower) / 2;
                if (NodeCount(middle) > options.MaxNodes)
                { lower = middle; }
                else
                { upper = middle; }
            }
            cellSize = upper;
        }
        int columns = (int)Math.Floor(width / cellSize) + 1;
        int rows = (int)Math.Floor(height / cellSize) + 1;
        if ((long)columns * rows > options.MaxNodes)
        {
            throw new PlanningException(
                "Слишком большая область расчёта. Увеличьте размер ячейки или уменьшите область.", PlanningErrorReason.ResourceLimit);
        }

        var nodes = new Coordinate[columns * rows];
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                nodes[y * columns + x] = new(
                    minimum.X + x * cellSize,
                    minimum.Y + y * cellSize);
            }
        }

        return new(nodes, columns, rows, minimum, cellSize);
    }
}
