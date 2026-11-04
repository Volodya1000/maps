using CableRoutePlanner.Domain;
using NetTopologySuite.Geometries;

namespace CableRoutePlanner.Infrastructure.Routing;

internal interface ITerrainRule
{
    TerrainKind Kind { get; }

    bool Blocks { get; }

    void Apply(Geometry intersection, long featureId, TerrainContribution contribution);
}

internal sealed class TerrainContribution
{
    public double Forest;

    public double Field;

    public double Clearing;

    public SortedSet<long> Roads { get; } = [];

    public SortedSet<long> Crossings { get; } = [];

    public SortedSet<long> BridgeCandidates { get; } = [];
}

internal sealed class TerrainRules(IEnumerable<ITerrainRule>? rules = null)
{
    private readonly Dictionary<TerrainKind, ITerrainRule> _byKind = (rules ?? Defaults)
        .ToDictionary(rule => rule.Kind);

    public bool TryGet(TerrainKind kind, out ITerrainRule rule) => _byKind.TryGetValue(kind, out rule!);

    public static IEnumerable<Point> PointComponents(Geometry geometry)
    {
        if (geometry.IsEmpty)
        {
            yield break;
        }
        if (geometry is Point point)
        {
            yield return point;
        }
        else if (geometry is GeometryCollection collection)
        {
            foreach (Geometry part in collection.Geometries)
            {
                foreach (Point component in PointComponents(part))
                {
                    yield return component;
                }
            }
        }
    }

    private static readonly ITerrainRule[] Defaults =
    [
        new BlockingTerrainRule(TerrainKind.Marsh),
        new BlockingTerrainRule(TerrainKind.Lake),
        new BlockingTerrainRule(TerrainKind.River),
        new BlockingTerrainRule(TerrainKind.Building),
        new LengthTerrainRule(TerrainKind.Forest, (length, contribution) => contribution.Forest += length),
        new LengthTerrainRule(TerrainKind.Field, (length, contribution) => contribution.Field += length),
        new LengthTerrainRule(TerrainKind.Clearing, (length, contribution) => contribution.Clearing += length),
        new PointCrossingRule(TerrainKind.Road, contribution => contribution.Roads),
        new IdentifierRule(TerrainKind.ApprovedCrossing, contribution => contribution.Crossings),
        new IdentifierRule(TerrainKind.BridgeCandidate, contribution => contribution.BridgeCandidates)
    ];

    private sealed class BlockingTerrainRule(TerrainKind kind) : ITerrainRule
    {
        public TerrainKind Kind => kind;

        public bool Blocks => TerrainTraversalPolicy.For(kind) == TerrainTraversalRule.Blocked;

        public void Apply(Geometry intersection, long featureId, TerrainContribution contribution)
        {
        }
    }

    private sealed class LengthTerrainRule(
        TerrainKind kind,
        Action<double, TerrainContribution> addLength) : ITerrainRule
    {
        public TerrainKind Kind => kind;

        public bool Blocks => false;

        public void Apply(Geometry intersection, long featureId, TerrainContribution contribution)
        {
            addLength(intersection.Length, contribution);
        }
    }

    private sealed class PointCrossingRule(
        TerrainKind kind,
        Func<TerrainContribution, SortedSet<long>> ids) : ITerrainRule
    {
        public TerrainKind Kind => kind;

        public bool Blocks => false;

        public void Apply(Geometry intersection, long featureId, TerrainContribution contribution)
        {
            if (PointComponents(intersection).Any())
            {
                ids(contribution).Add(featureId);
            }
        }
    }

    private sealed class IdentifierRule(
        TerrainKind kind,
        Func<TerrainContribution, SortedSet<long>> ids) : ITerrainRule
    {
        public TerrainKind Kind => kind;

        public bool Blocks => false;

        public void Apply(Geometry intersection, long featureId, TerrainContribution contribution) =>
            ids(contribution).Add(featureId);
    }
}
