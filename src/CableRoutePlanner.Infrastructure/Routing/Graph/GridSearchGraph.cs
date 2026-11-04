using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using NetTopologySuite.Geometries;

namespace CableRoutePlanner.Infrastructure.Routing;

/// <summary>Binds one leg and cost profile to a regular mesh and its explicit transition portals.</summary>
/// <remarks>
/// Grid nodes precede portal vertices, then the two arbitrary endpoints. Topology and
/// neighbour ordering match the original grid search. Every connector and portal edge
/// is checked in full; only grid-to-grid facts use the calculation's shared edge cache.
/// This object belongs to one synchronous search and is not registered in DI.
/// </remarks>
internal sealed class GridSearchGraph : IRoutingGraph
{
    private const int ConnectionRadiusCells = 8;
    private const int MaximumDirectConnectionCells = 2;
    private readonly GridMesh _mesh;
    private readonly TerrainEvaluator _evaluator;
    private readonly GridEdgeEvaluationCache _edges;
    private readonly Coordinate _start;
    private readonly Coordinate _destination;
    private readonly Coordinate[] _portalNodes;
    private readonly int _portalStartId;
    private readonly Dictionary<int, double> _fromStart;
    private readonly Dictionary<int, double> _toEnd;
    private readonly Dictionary<int, List<(int Id, double Cost)>> _portalConnections = [];
    private readonly Dictionary<int, List<(int Id, double Cost)>> _gridToPortals = [];
    private readonly double? _direct;

    public CostProfile Profile { get; }
    public int StartNodeId { get; }
    public int DestinationNodeId { get; }
    public int NodeCount => DestinationNodeId + 1;

    public GridSearchGraph(Coordinate start, Coordinate destination, GridMesh mesh, CostProfile profile,
        TerrainEvaluator evaluator, GridEdgeEvaluationCache edges, CancellationToken cancellationToken)
    {
        _start = start;
        _destination = destination;
        _mesh = mesh;
        Profile = profile;
        _evaluator = evaluator;
        _edges = edges;
        IReadOnlyList<GridPortal> portals = mesh.Portals ?? [];
        _portalNodes = portals.SelectMany(portal => portal.Coordinates).ToArray();
        _portalStartId = mesh.Nodes.Length;
        StartNodeId = _portalStartId + _portalNodes.Length;
        DestinationNodeId = StartNodeId + 1;
        (_fromStart, _toEnd, _direct) = PrepareConnections(portals, cancellationToken);
    }

    /// <summary>Prepares only endpoint/portal links; regular-grid facts remain lazy.</summary>
    /// <remarks>
    /// Keep start, destination, portal and direct-segment evaluation in this order.
    /// Connections are priced for this profile and owned by this graph; the shared
    /// edge cache stores profile-independent facts only. A second builder would
    /// need the same geometry/ID state without another consumer of these links.
    /// </remarks>
    private (Dictionary<int, double> FromStart, Dictionary<int, double> ToEnd, double? Direct)
        PrepareConnections(IReadOnlyList<GridPortal> portals, CancellationToken cancellationToken)
    {
        Dictionary<int, double> fromStart = Connect(_start, cancellationToken);
        Dictionary<int, double> toEnd = Connect(_destination, cancellationToken);
        ConnectPortals(portals, cancellationToken);
        double? direct = _start.Distance(_destination) <= _mesh.CellSizeMetres * MaximumDirectConnectionCells
            ? Cost(_start, _destination) : null;
        if ((fromStart.Count == 0 || toEnd.Count == 0) && direct is null)
        {
            throw new PlanningException(
                $"Точка не соединяется с узлами сетки {_mesh.CellSizeMetres:F0} м. "
                + "Это может быть препятствие либо ограничение разрешения сетки; физическая непроходимость не доказана.",
                PlanningErrorReason.GridConnectionUnavailable);
        }
        return (fromStart, toEnd, direct);
    }

    public Coordinate GetCoordinate(int id) => id == StartNodeId
        ? _start
        : id == DestinationNodeId ? _destination
            : id < _portalStartId ? _mesh.Nodes[id] : _portalNodes[id - _portalStartId];

    /// <summary>Uses the profile's minimum per-metre weight, so clearings cannot make A* overestimate.</summary>
    public double EstimateRemainingCost(int nodeId) =>
        GetCoordinate(nodeId).Distance(_destination) * Profile.MinimumCostPerMetre;

    public IEnumerable<(int Id, double Cost)> Neighbours(int nodeId)
    {
        if (nodeId == StartNodeId)
        {
            foreach (KeyValuePair<int, double> connection in _fromStart)
            { yield return (connection.Key, connection.Value); }
            if (_direct is { } directCost)
            { yield return (DestinationNodeId, directCost); }
            yield break;
        }

        if (_toEnd.TryGetValue(nodeId, out double finish))
        { yield return (DestinationNodeId, finish); }
        if (nodeId >= _portalStartId)
        {
            if (_portalConnections.TryGetValue(nodeId, out List<(int Id, double Cost)>? connections))
            {
                foreach ((int neighbour, double cost) in connections)
                { yield return (neighbour, cost); }
            }
            yield break;
        }

        if (_gridToPortals.TryGetValue(nodeId, out List<(int Id, double Cost)>? portalLinks))
        {
            foreach ((int portalId, double portalCost) in portalLinks)
            { yield return (portalId, portalCost); }
        }

        int column = nodeId % _mesh.Columns;
        int row = nodeId / _mesh.Columns;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if ((dx == 0 && dy == 0) || column + dx < 0 || column + dx >= _mesh.Columns
                    || row + dy < 0 || row + dy >= _mesh.Rows)
                { continue; }
                int next = (row + dy) * _mesh.Columns + column + dx;
                if (_edges.Get(nodeId, next)?.Cost(Profile) is { } cost)
                { yield return (next, cost); }
            }
        }
    }

    /// <summary>Connects an arbitrary endpoint to a bounded square of nearby grid nodes.</summary>
    /// <remarks>
    /// Eight cells preserve the existing coarse-grid reachability. Coordinates are
    /// rounded with ties-to-even to the nearest column/row, then clipped to the
    /// loaded mesh bounds. The square is enumerated row first, not sorted by distance.
    /// Every candidate segment must pass the same evaluator used for grid edges.
    /// </remarks>
    private Dictionary<int, double> Connect(Coordinate point, CancellationToken cancellationToken)
    {
        int column = (int)Math.Round((point.X - _mesh.Minimum.X) / _mesh.CellSizeMetres);
        int row = (int)Math.Round((point.Y - _mesh.Minimum.Y) / _mesh.CellSizeMetres);
        int firstRow = Math.Max(0, row - ConnectionRadiusCells);
        int lastRow = Math.Min(_mesh.Rows - 1, row + ConnectionRadiusCells);
        int firstColumn = Math.Max(0, column - ConnectionRadiusCells);
        int lastColumn = Math.Min(_mesh.Columns - 1, column + ConnectionRadiusCells);
        Dictionary<int, double> connections = [];
        for (int y = firstRow; y <= lastRow; y++)
        {
            for (int x = firstColumn; x <= lastColumn; x++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int id = y * _mesh.Columns + x;
                if (Cost(point, _mesh.Nodes[id]) is { } cost)
                { connections[id] = cost; }
            }
        }
        return connections;
    }

    private double? Cost(Coordinate from, Coordinate to) => _evaluator.EvaluateForSearch(from, to)?.Cost(Profile);

    private void ConnectPortals(IReadOnlyList<GridPortal> portals, CancellationToken cancellationToken)
    {
        int offset = 0;
        foreach (GridPortal portal in portals)
        {
            int firstId = _portalStartId + offset;
            for (int index = 1; index < portal.Coordinates.Length; index++)
            {
                int previous = firstId + index - 1;
                int current = firstId + index;
                if (Cost(GetCoordinate(previous), GetCoordinate(current)) is { } cost)
                {
                    AddPortalConnection(previous, current, cost);
                    AddPortalConnection(current, previous, cost);
                }
            }
            ConnectPortalEnd(firstId, cancellationToken);
            ConnectPortalEnd(firstId + portal.Coordinates.Length - 1, cancellationToken);
            offset += portal.Coordinates.Length;
        }
    }

    private void ConnectPortalEnd(int portalId, CancellationToken cancellationToken)
    {
        foreach (KeyValuePair<int, double> connection in Connect(GetCoordinate(portalId), cancellationToken))
        {
            AddPortalConnection(portalId, connection.Key, connection.Value);
            if (!_gridToPortals.TryGetValue(connection.Key, out List<(int Id, double Cost)>? reverse))
            {
                reverse = [];
                _gridToPortals[connection.Key] = reverse;
            }
            reverse.Add((portalId, connection.Value));
        }
    }

    private void AddPortalConnection(int from, int to, double cost)
    {
        if (!_portalConnections.TryGetValue(from, out List<(int Id, double Cost)>? connections))
        {
            connections = [];
            _portalConnections[from] = connections;
        }
        connections.Add((to, cost));
    }
}
