using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using NetTopologySuite.Geometries;

namespace CableRoutePlanner.Infrastructure.Routing;

/// <summary>A mapped transition polyline whose full segments and endpoint connectors must pass terrain checks.</summary>
public sealed record GridPortal(Coordinate[] Coordinates);

/// <summary>Row-major regular nodes with actual (possibly coarsened) spacing and optional mapped transitions.</summary>
/// <remarks>Coordinates and portal arrays are treated as read-only for the lifetime of a calculation.</remarks>
public sealed record GridMesh(
    Coordinate[] Nodes,
    int Columns,
    int Rows,
    Coordinate Minimum,
    double CellSizeMetres,
    IReadOnlyList<GridPortal>? Portals = null);

/// <summary>Substitutable graph-layout generator; terrain permissions are evaluated after mesh creation.</summary>
public interface IGridGenerator
{
    GridMesh Generate(GeoBounds bounds, RoutingOptions options, LocalProjection projection);
}

/// <summary>Builds a row-major, eight-neighbour mesh inside the loaded search rectangle.</summary>
/// <remarks>Adaptive spacing bounds node allocations, not obstacle precision or physical reachability.</remarks>
public sealed class GridGenerator : IGridGenerator
{
    // Tiny meshes cannot represent a long route after coarsening; reject instead of
    // producing a misleading sparse path. Keep the historical threshold for compatibility.
    private const int MinimumNodesForAdaptiveCoarsening = 1000;

    // Upper bound for the overview graph; wider cells risk losing narrow passages.
    private const double MaximumAdaptiveCellSizeMetres = 2_000;

    private const int CellSizeSearchIterations = 48;

    /// <summary>Projects bounds and allocates a regular mesh within the configured node budget.</summary>
    /// <param name="bounds">The package-clipped area whose source geometries have been loaded.</param>
    /// <param name="options">Validated requested spacing and maximum node count.</param>
    /// <param name="projection">The same local metre projection used by terrain and route geometry.</param>
    /// <returns>Nodes in stable row-major order, without terrain filtering.</returns>
    /// <exception cref="PlanningException">The area cannot fit within the permitted spacing and node budget.</exception>
    public GridMesh Generate(GeoBounds bounds, RoutingOptions options, LocalProjection projection)
    {
        Coordinate minimum = projection.Forward(new GeoCoordinate(bounds.South, bounds.West));
        Coordinate maximum = projection.Forward(new GeoCoordinate(bounds.North, bounds.East));
        double width = maximum.X - minimum.X;
        double height = maximum.Y - minimum.Y;
        double cellSize = SelectCellSize(width, height, options);
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

    /// <summary>Finds a bounded overview spacing using actual integer row/column counts.</summary>
    /// <remarks>
    /// sqrt(area / budget) is an initial estimate: the endpoint row and column can
    /// exceed that budget. A 48-step binary search keeps a valid upper bound and
    /// refines only when 2000 m cells fit. Otherwise Generate reports ResourceLimit.
    /// Existing very small budgets retain their early-rejection behavior.
    /// </remarks>
    private static double SelectCellSize(double width, double height, RoutingOptions options)
    {
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
        return cellSize;
    }
}
