using CableRoutePlanner.Domain;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;
using NetTopologySuite.Triangulate.Polygon;
using NetTopologySuite.Triangulate.Tri;

namespace CableRoutePlanner.Infrastructure.Routing;

/// <summary>Provides bounded surface search vertices where a regular mesh misses a narrow passage.</summary>
/// <remarks>Free-space geometry proposes edges; the existing full-segment evaluator alone permits them.</remarks>
internal sealed class SurfaceDetourSearch(TerrainEvaluator evaluator, Envelope bounds, int maximumNodes)
{
    // This clearance shapes candidate search space; it grants no terrain permission.
    private const double SurfaceClearanceMetres = 0.2;
    private const int MaximumSourceFeatures = 20_000;
    private const int MaximumSourceCoordinates = 100_000;
    private const int MaximumPreparedCoordinates = 200_000;
    private const int SourceCoordinatesPerNode = 4;
    private const int PreparedCoordinatesPerNode = 8;

    private sealed record SurfaceLink(int Next, Coordinate Midpoint, EdgeEvaluation First, EdgeEvaluation Second);
    private sealed record SurfaceMesh(Polygon Component, Coordinate[] Centres, SurfaceLink[][] Links, STRtree<int> TriangleIndex, Polygon[] Triangles);

    private readonly GeometryFactory _factory = new();
    private Geometry? _freeSpace;
    private SurfaceMesh? _mesh;
    private bool _preparationAttempted;
    private bool _preparationResourceLimited;
    private int _inputFeatureCount;

    public int NodeCount { get; private set; }
    public int ExpandedNodes { get; private set; }
    public int InputVertexCount { get; private set; }
    public bool ResourceLimited { get; private set; }

    public List<Coordinate>? FindPath(Coordinate start, Coordinate end, CostProfile profile,
        IGridPathfinder pathfinder, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        NodeCount = 0;
        ExpandedNodes = 0;
        ResourceLimited = false;
        if (!bounds.Covers(start) || !bounds.Covers(end))
        {
            return null;
        }
        if (maximumNodes < 3)
        {
            ResourceLimited = true;
            return null;
        }

        if (!_preparationAttempted)
        {
            _freeSpace = PrepareFreeSpace(ct);
            _preparationResourceLimited = ResourceLimited;
            _preparationAttempted = true;
        }
        if (_freeSpace is null)
        {
            ResourceLimited = _preparationResourceLimited;
            return null;
        }

        Point first = _factory.CreatePoint(start);
        Point last = _factory.CreatePoint(end);
        if (_mesh is null || !_mesh.Component.Covers(first) || !_mesh.Component.Covers(last))
        {
            Polygon? component = FindComponent(_freeSpace, first, last, ct);
            if (component is null)
            {
                return null;
            }
            _mesh = CreateMesh(component, ct);
        }
        if (_mesh is null)
        {
            return null;
        }

        SurfaceGraph graph = new(_mesh, evaluator, start, end, profile, bounds, ct);
        NodeCount = graph.NodeCount;
        IReadOnlyList<int>? path;
        try
        {
            path = pathfinder.FindPath(graph, ct);
        }
        finally
        {
            ExpandedNodes = graph.ExpandedNodes;
        }
        return path is null ? null : graph.ToCoordinates(path, ct);
    }

    private Geometry? PrepareFreeSpace(CancellationToken ct)
    {
        int sourceBudget = (int)Math.Min((long)maximumNodes * SourceCoordinatesPerNode, MaximumSourceCoordinates);
        List<TerrainObstacle> obstacles = [];
        foreach (TerrainObstacle obstacle in evaluator.Terrain.Index.Query(bounds).OrderBy(obstacle => obstacle.Id))
        {
            ct.ThrowIfCancellationRequested();
            if (obstacle.Kind != TerrainKind.Road && TerrainTraversalPolicy.For(obstacle.Kind) != TerrainTraversalRule.Blocked)
            {
                continue;
            }
            if (!AddInput(obstacle.Geometry.NumPoints, sourceBudget))
            {
                ResourceLimited = true;
                return null;
            }
            obstacles.Add(obstacle);
        }

        List<Geometry> openings = [];
        foreach ((long _, Geometry _, Geometry corridor) in evaluator.Terrain.BridgeCrossingCandidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!bounds.Intersects(corridor.EnvelopeInternal))
            {
                continue;
            }
            if (!AddInput(corridor.NumPoints, sourceBudget))
            {
                return null;
            }
            openings.Add(corridor);
        }
        IReadOnlyList<GridPortal> gasPortals = evaluator.Terrain.GasCrossings.Portals;
        foreach (GridPortal portal in gasPortals)
        {
            ct.ThrowIfCancellationRequested();
            if (!AddInput(portal.Coordinates.Length, sourceBudget))
            {
                return null;
            }
        }

        Geometry area = _factory.ToGeometry(bounds);
        if (evaluator.Terrain.Coverage is { } coverage
            && !(evaluator.Terrain.FullyCoveredArea?.Covers(bounds) ?? false))
        {
            if (!AddInput(coverage.Geometry.NumPoints, sourceBudget))
            {
                return null;
            }
            ct.ThrowIfCancellationRequested();
            area = area.Intersection(coverage.Geometry);
            ct.ThrowIfCancellationRequested();
        }
        if (!WithinPreparedBudget(area.NumPoints))
        {
            return null;
        }

        long openingCoordinates = openings.Sum(opening => (long)opening.NumPoints);
        foreach (GridPortal portal in gasPortals)
        {
            ct.ThrowIfCancellationRequested();
            LineString line = _factory.CreateLineString(portal.Coordinates);
            Geometry opening = line.Buffer(SurfaceClearanceMetres);
            ct.ThrowIfCancellationRequested();
            openingCoordinates += opening.NumPoints;
            if (!WithinPreparedBudget(openingCoordinates))
            {
                return null;
            }
            openings.Add(opening);
        }

        STRtree<Geometry> openingIndex = new();
        foreach (Geometry opening in openings)
        {
            openingIndex.Insert(opening.EnvelopeInternal, opening);
        }
        openingIndex.Build();
        List<Geometry>? blocked = BufferObstacles(obstacles, area, openingIndex, ct);
        if (blocked is null)
        {
            return null;
        }
        ct.ThrowIfCancellationRequested();
        if (blocked.Count > 0)
        {
            // NG noding avoids the legacy union's costly near-coincident snapping fallback.
            Geometry union = UnaryUnionNG.Union(blocked, _factory, _factory.PrecisionModel);
            ct.ThrowIfCancellationRequested();
            if (!WithinPreparedBudget(union.NumPoints))
            {
                return null;
            }
            area = OverlayNG.Overlay(area, union, SpatialFunction.Difference, _factory.PrecisionModel);
            ct.ThrowIfCancellationRequested();
        }
        if (!WithinPreparedBudget(area.NumPoints))
        {
            return null;
        }
        return area;
    }

    private List<Geometry>? BufferObstacles(List<TerrainObstacle> obstacles, Geometry area,
        STRtree<Geometry> openings, CancellationToken ct)
    {
        List<Geometry> blocked = [];
        long coordinates = 0;
        foreach (TerrainObstacle obstacle in obstacles)
        {
            ct.ThrowIfCancellationRequested();
            Geometry clipped = obstacle.Geometry.Intersection(area);
            ct.ThrowIfCancellationRequested();
            if (clipped.IsEmpty)
            {
                continue;
            }
            if (!WithinPreparedBudget(clipped.NumPoints))
            {
                return null;
            }
            Geometry buffered = clipped.Buffer(SurfaceClearanceMetres);
            ct.ThrowIfCancellationRequested();
            if (!WithinPreparedBudget(coordinates + buffered.NumPoints))
            {
                return null;
            }
            if (obstacle.Kind == TerrainKind.Road)
            {
                foreach (Geometry opening in openings.Query(buffered.EnvelopeInternal))
                {
                    ct.ThrowIfCancellationRequested();
                    buffered = buffered.Difference(opening);
                    ct.ThrowIfCancellationRequested();
                    if (!WithinPreparedBudget(coordinates + buffered.NumPoints))
                    {
                        return null;
                    }
                }
            }
            coordinates += buffered.NumPoints;
            if (!WithinPreparedBudget(coordinates))
            {
                return null;
            }
            if (!buffered.IsEmpty)
            {
                blocked.Add(buffered);
            }
        }
        return blocked;
    }

    private bool AddInput(int vertices, int budget)
    {
        InputVertexCount = (int)Math.Min((long)InputVertexCount + vertices, int.MaxValue);
        _inputFeatureCount++;
        if (InputVertexCount <= budget && _inputFeatureCount <= MaximumSourceFeatures)
        {
            return true;
        }
        ResourceLimited = true;
        return false;
    }

    private bool WithinPreparedBudget(long vertices)
    {
        if (vertices <= Math.Min((long)maximumNodes * PreparedCoordinatesPerNode, MaximumPreparedCoordinates))
        {
            return true;
        }
        ResourceLimited = true;
        return false;
    }

    private static Polygon? FindComponent(Geometry geometry, Point start, Point end, CancellationToken ct)
    {
        foreach (Polygon polygon in Polygons(geometry))
        {
            ct.ThrowIfCancellationRequested();
            if (polygon.Covers(start) && polygon.Covers(end))
            {
                return polygon;
            }
        }
        return null;
    }

    private static IEnumerable<Polygon> Polygons(Geometry geometry)
    {
        if (geometry is Polygon polygon)
        {
            yield return polygon;
        }
        else if (geometry is GeometryCollection collection)
        {
            foreach (Geometry part in collection.Geometries)
            {
                foreach (Polygon component in Polygons(part))
                {
                    yield return component;
                }
            }
        }
    }

    private SurfaceMesh? CreateMesh(Polygon component, CancellationToken ct)
    {
        // A polygon with holes has at most V + 2H - 2 triangles; NumPoints includes each closing vertex.
        long expectedTriangles = (long)component.NumPoints + component.NumInteriorRings - 3;
        if (expectedTriangles + 2 > maximumNodes)
        {
            ResourceLimited = true;
            return null;
        }
        ct.ThrowIfCancellationRequested();
        ConstrainedDelaunayTriangulator triangulator = new(component);
        IList<Tri> triangles = triangulator.GetTriangles();
        ct.ThrowIfCancellationRequested();
        if ((long)triangles.Count + 2 > maximumNodes)
        {
            ResourceLimited = true;
            return null;
        }

        Coordinate[] centres = new Coordinate[triangles.Count];
        Polygon[] polygons = new Polygon[triangles.Count];
        List<SurfaceLink>[] links = new List<SurfaceLink>[triangles.Count];
        STRtree<int> index = new();
        for (int i = 0; i < triangles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            Tri triangle = triangles[i];
            Coordinate first = triangle.GetCoordinate(0);
            Coordinate second = triangle.GetCoordinate(1);
            Coordinate third = triangle.GetCoordinate(2);
            centres[i] = new((first.X + second.X + third.X) / 3,
                (first.Y + second.Y + third.Y) / 3);
            polygons[i] = _factory.CreatePolygon([first, second, third, first]);
            index.Insert(polygons[i].EnvelopeInternal, i);
            links[i] = [];
        }
        index.Build();
        Dictionary<(double AX, double AY, double BX, double BY), int> edges = [];
        for (int i = 0; i < triangles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            for (int edge = 0; edge < 3; edge++)
            {
                Coordinate a = triangles[i].GetCoordinate(edge);
                Coordinate b = triangles[i].GetCoordinate((edge + 1) % 3);
                if (a.CompareTo(b) > 0)
                {
                    (a, b) = (b, a);
                }
                (double, double, double, double) key = (a.X, a.Y, b.X, b.Y);
                if (edges.TryGetValue(key, out int other))
                {
                    Coordinate midpoint = new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                    EdgeEvaluation? first = EvaluateSurfaceEdge(centres[other], midpoint);
                    EdgeEvaluation? second = EvaluateSurfaceEdge(midpoint, centres[i]);
                    ct.ThrowIfCancellationRequested();
                    if (first is not null && second is not null)
                    {
                        links[other].Add(new(i, midpoint, first, second));
                        links[i].Add(new(other, midpoint, second, first));
                    }
                }
                else
                {
                    edges.Add(key, i);
                }
            }
        }
        return new(component, centres, links.Select(list => list.OrderBy(link => link.Next).ToArray()).ToArray(), index, polygons);
    }

    private EdgeEvaluation? EvaluateSurfaceEdge(Coordinate from, Coordinate to) =>
        bounds.Covers(from) && bounds.Covers(to) ? evaluator.EvaluateForSearch(from, to) : null;

    private sealed class SurfaceGraph : IRoutingGraph
    {
        private readonly SurfaceMesh _mesh;
        private readonly Coordinate _start;
        private readonly Coordinate _end;
        private readonly CostProfile _profile;
        private readonly Envelope _bounds;
        private readonly Dictionary<int, double> _fromStart;
        private readonly Dictionary<int, double> _toEnd;

        public int NodeCount => _mesh.Centres.Length + 2;
        public int StartNodeId => NodeCount - 2;
        public int DestinationNodeId => NodeCount - 1;
        public int ExpandedNodes { get; private set; }

        public SurfaceGraph(SurfaceMesh mesh, TerrainEvaluator terrain, Coordinate start, Coordinate end,
            CostProfile profile, Envelope queryBounds, CancellationToken ct)
        {
            _mesh = mesh;
            _start = start;
            _end = end;
            _profile = profile;
            _bounds = queryBounds;
            _fromStart = Connect(start, terrain, ct);
            _toEnd = Connect(end, terrain, ct);
        }

        private Dictionary<int, double> Connect(Coordinate point, TerrainEvaluator terrain, CancellationToken ct)
        {
            Dictionary<int, double> result = [];
            Point endpoint = _mesh.Component.Factory.CreatePoint(point);
            foreach (int triangle in _mesh.TriangleIndex.Query(endpoint.EnvelopeInternal).OrderBy(id => id))
            {
                ct.ThrowIfCancellationRequested();
                if (_mesh.Triangles[triangle].Covers(endpoint)
                    && _bounds.Covers(point) && _bounds.Covers(_mesh.Centres[triangle])
                    && terrain.EvaluateForSearch(point, _mesh.Centres[triangle]) is { } edge)
                {
                    result.Add(triangle, edge.Cost(_profile));
                }
            }
            return result;
        }

        public double EstimateRemainingCost(int nodeId) =>
            CoordinateAt(nodeId).Distance(_end) * _profile.MinimumCostPerMetre;

        public IEnumerable<(int Id, double Cost)> Neighbours(int nodeId)
        {
            ExpandedNodes++;
            if (nodeId == StartNodeId)
            {
                foreach (KeyValuePair<int, double> link in _fromStart)
                {
                    yield return (link.Key, link.Value);
                }
                yield break;
            }
            if (nodeId == DestinationNodeId)
            {
                yield break;
            }
            if (_toEnd.TryGetValue(nodeId, out double finish))
            {
                yield return (DestinationNodeId, finish);
            }
            foreach (SurfaceLink link in _mesh.Links[nodeId])
            {
                yield return (link.Next, link.First.Cost(_profile) + link.Second.Cost(_profile));
            }
        }

        private Coordinate CoordinateAt(int id) => id == StartNodeId ? _start
            : id == DestinationNodeId ? _end : _mesh.Centres[id];

        public List<Coordinate> ToCoordinates(IReadOnlyList<int> path, CancellationToken ct)
        {
            List<Coordinate> result = [];
            for (int i = 0; i < path.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                int current = path[i];
                if (i > 0 && path[i - 1] < _mesh.Centres.Length && current < _mesh.Centres.Length)
                {
                    result.Add(_mesh.Links[path[i - 1]].First(link => link.Next == current).Midpoint);
                }
                result.Add(CoordinateAt(current));
            }
            return result;
        }
    }
}
