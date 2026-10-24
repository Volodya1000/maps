using CableRoutePlanner.Domain;
using NetTopologySuite.Geometries;

namespace CableRoutePlanner.Infrastructure.Routing;

internal interface ITerrainRule
{
    TerrainKind Kind
    {
        get;
    }

    bool Blocks
    {
        get;
    }

    void Apply(Geometry intersection, long featureId, TerrainContribution contribution);
}

internal sealed class TerrainContribution
{
    public double Forest;

    public double Field;

    public double Clearing;

    public List<long> Roads { get; } = [];

    public List<long> Crossings { get; } = [];

    public List<long> BridgeCandidates { get; } = [];
}

internal sealed class TerrainRules(IEnumerable<ITerrainRule>? rules = null)
{
    private readonly Dictionary<TerrainKind, ITerrainRule> _byKind = (rules ?? Defaults)
        .ToDictionary(rule => rule.Kind);

    public bool TryGet(TerrainKind kind, out ITerrainRule rule) => _byKind.TryGetValue(kind, out rule!);

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

        public bool Blocks => true;

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
        Func<TerrainContribution, List<long>> ids) : ITerrainRule
    {
        public TerrainKind Kind => kind;

        public bool Blocks => false;

        public void Apply(Geometry intersection, long featureId, TerrainContribution contribution)
        {
            if (intersection.Dimension == Dimension.Point)
            {
                ids(contribution).Add(featureId);
            }
        }
    }

    private sealed class IdentifierRule(
        TerrainKind kind,
        Func<TerrainContribution, List<long>> ids) : ITerrainRule
    {
        public TerrainKind Kind => kind;

        public bool Blocks => false;

        public void Apply(Geometry intersection, long featureId, TerrainContribution contribution) =>
            ids(contribution).Add(featureId);
    }
}
