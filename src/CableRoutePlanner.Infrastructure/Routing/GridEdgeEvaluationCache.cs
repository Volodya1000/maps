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

    /// <summary>Unique geometric evaluations, including blocked edges, in this calculation.</summary>
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
}
