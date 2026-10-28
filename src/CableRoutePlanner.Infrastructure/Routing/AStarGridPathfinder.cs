using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using NetTopologySuite.Geometries;

namespace CableRoutePlanner.Infrastructure.Routing;

public interface IGridPathfinder
{
    IReadOnlyList<Coordinate> FindPath(
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
    public IReadOnlyList<Coordinate> FindPath(
        Coordinate start,
        Coordinate destination,
        GridMesh mesh,
        CostProfile profile,
        Func<Coordinate, Coordinate, double?> evaluate,
        Func<int, int, double?> gridEdgeCost,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<GridPortal> portals = mesh.Portals ?? [];
        Coordinate[] portalNodes = portals.SelectMany(portal => portal.Coordinates).ToArray();
        int portalStartId = mesh.Nodes.Length;
        int startId = portalStartId + portalNodes.Length;
        int endId = startId + 1;
        Coordinate Node(int id) => id == startId
            ? start
            : id == endId
                ? destination
                : id < portalStartId ? mesh.Nodes[id] : portalNodes[id - portalStartId];

        Dictionary<int, double> Connect(Coordinate point)
        {
            int column = (int)Math.Round((point.X - mesh.Minimum.X) / mesh.CellSizeMetres);
            int row = (int)Math.Round((point.Y - mesh.Minimum.Y) / mesh.CellSizeMetres);
            var connections = new Dictionary<int, double>();
            for (int y = Math.Max(0, row - 1); y <= Math.Min(mesh.Rows - 1, row + 1); y++)
            {
                for (int x = Math.Max(0, column - 1); x <= Math.Min(mesh.Columns - 1, column + 1); x++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int id = y * mesh.Columns + x;
                    if (evaluate(point, mesh.Nodes[id]) is { } cost)
                    {
                        connections[id] = cost;
                    }
                }
            }

            return connections;
        }

        Dictionary<int, double> fromStart = Connect(start);
        Dictionary<int, double> toEnd = Connect(destination);
        var portalConnections = new Dictionary<int, List<(int Id, double Cost)>>();
        var gridToPortals = new Dictionary<int, List<(int Id, double Cost)>>();
        void AddPortalConnection(int from, int to, double cost)
        {
            if (!portalConnections.TryGetValue(from, out List<(int Id, double Cost)>? connections))
            {
                connections = [];
                portalConnections[from] = connections;
            }

            connections.Add((to, cost));
        }

        void ConnectPortalEnd(int portalId)
        {
            foreach (KeyValuePair<int, double> connection in Connect(Node(portalId)))
            {
                AddPortalConnection(portalId, connection.Key, connection.Value);
                if (!gridToPortals.TryGetValue(connection.Key, out List<(int Id, double Cost)>? reverse))
                {
                    reverse = [];
                    gridToPortals[connection.Key] = reverse;
                }

                reverse.Add((portalId, connection.Value));
            }
        }

        int portalOffset = 0;
        foreach (GridPortal portal in portals)
        {
            int firstId = portalStartId + portalOffset;
            for (int index = 1; index < portal.Coordinates.Length; index++)
            {
                int previousId = firstId + index - 1;
                int currentId = firstId + index;
                if (evaluate(Node(previousId), Node(currentId)) is { } cost)
                {
                    AddPortalConnection(previousId, currentId, cost);
                    AddPortalConnection(currentId, previousId, cost);
                }
            }

            ConnectPortalEnd(firstId);
            ConnectPortalEnd(firstId + portal.Coordinates.Length - 1);
            portalOffset += portal.Coordinates.Length;
        }

        double? direct = start.Distance(destination) <= mesh.CellSizeMetres * 2 ? evaluate(start, destination) : null;
        if ((fromStart.Count == 0 || toEnd.Count == 0) && direct is null)
        {
            throw new PlanningException(
                $"Точка не соединяется с узлами сетки {mesh.CellSizeMetres:F0} м. "
                + "Это может быть препятствие либо ограничение разрешения сетки; физическая непроходимость не доказана.",
                PlanningErrorReason.GridConnectionUnavailable);
        }

        IEnumerable<(int Id, double Cost)> Neighbours(int id)
        {
            if (id == startId)
            {
                foreach (KeyValuePair<int, double> connection in fromStart)
                {
                    yield return (connection.Key, connection.Value);
                }

                if (direct is { } directCost)
                {
                    yield return (endId, directCost);
                }

                yield break;
            }

            if (toEnd.TryGetValue(id, out double finish))
            {
                yield return (endId, finish);
            }

            if (id >= portalStartId)
            {
                if (portalConnections.TryGetValue(id, out List<(int Id, double Cost)>? connections))
                {
                    foreach ((int portalNeighbour, double portalCost) in connections)
                    {
                        yield return (portalNeighbour, portalCost);
                    }
                }

                yield break;
            }

            if (gridToPortals.TryGetValue(id, out List<(int Id, double Cost)>? portalLinks))
            {
                foreach ((int portalId, double portalCost) in portalLinks)
                {
                    yield return (portalId, portalCost);
                }
            }

            int x = id % mesh.Columns;
            int y = id / mesh.Columns;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if ((dx == 0 && dy == 0)
                        || x + dx < 0 || x + dx >= mesh.Columns
                        || y + dy < 0 || y + dy >= mesh.Rows)
                    {
                        continue;
                    }

                    int next = (y + dy) * mesh.Columns + x + dx;
                    if (gridEdgeCost(id, next) is { } cost)
                    {
                        yield return (next, cost);
                    }
                }
            }
        }

        var queue = new PriorityQueue<(int Id, double Cost), double>();
        double[] distances = Enumerable.Repeat(double.PositiveInfinity, mesh.Nodes.Length + portalNodes.Length + 2).ToArray();
        int[] parents = Enumerable.Repeat(-1, distances.Length).ToArray();
        distances[startId] = 0;
        queue.Enqueue((startId, 0), 0);
        while (queue.TryDequeue(out (int Id, double Cost) current, out _))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.Cost > distances[current.Id])
            {
                continue;
            }

            if (current.Id == endId)
            {
                var path = new List<Coordinate>();
                for (int id = endId; id != -1; id = parents[id])
                {
                    path.Add(Node(id));
                }

                path.Reverse();
                return path;
            }

            foreach ((int next, double edgeCost) in Neighbours(current.Id))
            {
                double cost = current.Cost + edgeCost;
                if (cost >= distances[next])
                {
                    continue;
                }

                distances[next] = cost;
                parents[next] = current.Id;
                // Terrain penalties are nonnegative; the profile's clearing multiplier is its
                // lowest possible per-metre cost, so this remains an admissible lower bound.
                queue.Enqueue((next, cost), cost + Node(next).Distance(destination) * profile.MinimumCostPerMetre);
            }
        }

        throw new PlanningException(
            "В доступной области нет допустимого маршрута. "
            + $"Сетка {mesh.CellSizeMetres:F0} м может не представить узкий проход; алгоритм не гарантирует полноту. "
            + "Можно увеличить область данных или изменить точки.",
            PlanningErrorReason.NoRouteInSearchArea);
    }
}
