using NetTopologySuite.Geometries;

namespace CableRoutePlanner.Infrastructure.Routing;

/// <summary>Owns geometric edge facts for one calculation, shared by its legs and cost profiles.</summary>
/// <remarks>
/// Edges are undirected. A null evaluation is a cached blocked edge, not a cache miss.
/// The calculation evaluates profiles sequentially; independent requests own independent instances.
/// Costs are calculated from these facts by Domain and never stored here.
/// </remarks>
internal sealed class GridEdgeEvaluationCache(GridMesh mesh, TerrainEvaluator evaluator)
{
    private readonly Dictionary<(int From, int To), EdgeEvaluation?> _evaluations = [];
    private readonly Dictionary<(double X, double Y, int Node), EdgeEvaluation?> _connectors = [];

    /// <summary>Unique regular-grid evaluations, including blocked edges, in this calculation.</summary>
    public int EvaluationCount => _evaluations.Count;

    public EdgeEvaluation? Get(int from, int to)
    {
        (int, int) key = from < to ? (from, to) : (to, from);
        if (!_evaluations.TryGetValue(key, out EdgeEvaluation? edge))
        {
            _evaluations[key] = edge = evaluator.EvaluateForSearch(mesh.Nodes[from], mesh.Nodes[to]);
        }
        return edge;
    }

    /// <summary>Reuses full connector facts across cost profiles without caching profile-dependent prices.</summary>
    public EdgeEvaluation? GetConnector(Coordinate point, int gridNode)
    {
        (double, double, int) key = (point.X, point.Y, gridNode);
        if (_connectors.TryGetValue(key, out EdgeEvaluation? edge))
        {
            return edge;
        }

        edge = evaluator.EvaluateForSearch(point, mesh.Nodes[gridNode]);
        // Keep connector storage within the calculation's existing grid-node budget.
        if (_connectors.Count < mesh.Nodes.Length)
        {
            _connectors.Add(key, edge);
        }
        return edge;
    }
}
