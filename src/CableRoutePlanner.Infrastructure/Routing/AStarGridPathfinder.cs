using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using NetTopologySuite.Geometries;

namespace CableRoutePlanner.Infrastructure.Routing;

public interface IGridPathfinder
{
    List<Coordinate> FindPath(
        Coordinate start,
        Coordinate destination,
        GridMesh mesh,
        CostProfile profile,
        Func<Coordinate, Coordinate, double?> evaluate,
        Func<int, int, double?> gridEdgeCost,
        CancellationToken cancellationToken);
}

public sealed class AStarGridPathfinder : IGridPathfinder
{
    public List<Coordinate> FindPath(
        Coordinate start,
        Coordinate destination,
        GridMesh mesh,
        CostProfile profile,
        Func<Coordinate, Coordinate, double?> evaluate,
        Func<int, int, double?> gridEdgeCost,
        CancellationToken cancellationToken)
    {
        var startId = mesh.Nodes.Length;
        var endId = startId + 1;
        Coordinate Node(int id) => id == startId ? start : id == endId ? destination : mesh.Nodes[id];

        Dictionary<int, double> Connect(Coordinate point)
        {
            var column = (int)Math.Round((point.X - mesh.Minimum.X) / mesh.CellSizeMetres);
            var row = (int)Math.Round((point.Y - mesh.Minimum.Y) / mesh.CellSizeMetres);
            var connections = new Dictionary<int, double>();
            for (var y = Math.Max(0, row - 1); y <= Math.Min(mesh.Rows - 1, row + 1); y++)
            {
                for (var x = Math.Max(0, column - 1); x <= Math.Min(mesh.Columns - 1, column + 1); x++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var id = y * mesh.Columns + x;
                    if (evaluate(point, mesh.Nodes[id]) is { } cost)
                    {
                        connections[id] = cost;
                    }
                }
            }

            return connections;
        }

        var fromStart = Connect(start);
        var toEnd = Connect(destination);
        var direct = start.Distance(destination) <= mesh.CellSizeMetres * 2 ? evaluate(start, destination) : null;
        if (fromStart.Count == 0 || toEnd.Count == 0)
        {
            throw new PlanningException(
                "Точка находится в препятствии или не соединяется с допустимой областью сетки.");
        }

        IEnumerable<(int Id, double Cost)> Neighbours(int id)
        {
            if (id == startId)
            {
                foreach (var connection in fromStart)
                {
                    yield return (connection.Key, connection.Value);
                }

                if (direct is { } directCost)
                {
                    yield return (endId, directCost);
                }

                yield break;
            }

            if (toEnd.TryGetValue(id, out var finish))
            {
                yield return (endId, finish);
            }

            var x = id % mesh.Columns;
            var y = id / mesh.Columns;
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if ((dx == 0 && dy == 0)
                        || x + dx < 0 || x + dx >= mesh.Columns
                        || y + dy < 0 || y + dy >= mesh.Rows)
                    {
                        continue;
                    }

                    var next = (y + dy) * mesh.Columns + x + dx;
                    if (gridEdgeCost(id, next) is { } cost)
                    {
                        yield return (next, cost);
                    }
                }
            }
        }

        var queue = new PriorityQueue<(int Id, double Cost), double>();
        var distances = Enumerable.Repeat(double.PositiveInfinity, mesh.Nodes.Length + 2).ToArray();
        var parents = Enumerable.Repeat(-1, distances.Length).ToArray();
        distances[startId] = 0;
        queue.Enqueue((startId, 0), 0);
        while (queue.TryDequeue(out var current, out _))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.Cost > distances[current.Id])
            {
                continue;
            }

            if (current.Id == endId)
            {
                var path = new List<Coordinate>();
                for (var id = endId; id != -1; id = parents[id])
                {
                    path.Add(Node(id));
                }

                path.Reverse();
                return path;
            }

            foreach (var (next, edgeCost) in Neighbours(current.Id))
            {
                var cost = current.Cost + edgeCost;
                if (cost >= distances[next])
                {
                    continue;
                }

                distances[next] = cost;
                parents[next] = current.Id;
                // 0.49 is below the smallest profile multiplier, with margin for projection error.
                queue.Enqueue((next, cost), cost + Node(next).Distance(destination) * 0.49);
            }
        }

        throw new PlanningException(
            "В доступной области нет допустимого маршрута. "
            + "Река требует разрешённого или потенциального перехода. "
            + "Можно увеличить область данных или изменить точки.");
    }
}
