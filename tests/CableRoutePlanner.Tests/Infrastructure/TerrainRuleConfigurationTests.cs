using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure;
using CableRoutePlanner.Infrastructure.Geography;
using CableRoutePlanner.Infrastructure.Routing;
using NetTopologySuite.Geometries;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class TerrainRuleConfigurationTests
{
    public static IEnumerable<object[]> Kinds => Enum.GetValues<TerrainKind>().Select(kind => new object[] { kind });

    [Theory]
    [MemberData(nameof(Kinds))]
    public void EveryDeclaredTerrainRequiresAnExplicitRule(TerrainKind omitted)
    {
        ITerrainRule[] rules = Enum.GetValues<TerrainKind>().Where(kind => kind != omitted)
            .Select(kind => (ITerrainRule)new ConfiguredRule(kind, TerrainTraversalPolicy.For(kind) == TerrainTraversalRule.Blocked))
            .ToArray();
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => new TerrainRules(rules));
        Assert.Equal("rules", error.ParamName);
        Assert.Equal(omitted, error.ActualValue);
    }

    [Theory]
    [InlineData(TerrainKind.Marsh, false)]
    [InlineData(TerrainKind.Forest, true)]
    public void RuleBlockingMustMatchTheDomainDecision(TerrainKind conflicting, bool blocks)
    {
        ITerrainRule[] rules = Enum.GetValues<TerrainKind>()
            .Select(kind => (ITerrainRule)new ConfiguredRule(kind, kind == conflicting
                ? blocks : TerrainTraversalPolicy.For(kind) == TerrainTraversalRule.Blocked)).ToArray();
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => new TerrainRules(rules));
        Assert.Equal("rules", error.ParamName);
        Assert.Equal(conflicting, error.ActualValue);
    }

    [Fact]
    public void UnknownTerrainCannotBecomeNeutralByDefault()
    {
        TerrainKind unknown = (TerrainKind)999;
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => TerrainTraversalPolicy.For(unknown));
        Assert.Equal("terrain", error.ParamName);
        Assert.Equal(unknown, error.ActualValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedInputIsRejectedBeforeAnySearch(bool intersects)
    {
        TerrainKind unknown = (TerrainKind)999;
        MapFeature feature = TestData.Polygon(1, unknown, 10, 10, 30, 30);
        LocalProjection projection = new(TestData.P(0, 0));
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            TerrainEvaluator evaluator = new([feature], projection);
            evaluator.Evaluate(new(0, intersects ? 20 : 50), new(40, intersects ? 20 : 50));
        });
        Assert.Equal("terrain", error.ParamName);
        Assert.Equal(unknown, error.ActualValue);
    }

    [Fact]
    public async Task PointPlacementRejectsTheSameUnsupportedTerrainAsRouting()
    {
        TerrainKind unknown = (TerrainKind)999;
        GeodataPointPlacementValidator validator = new(new MemoryData(TestData.Polygon(1, unknown, 10, 10, 30, 30)));
        ArgumentOutOfRangeException error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => validator.ValidateAsync(TestData.P(20, 20), default));
        Assert.Equal("terrain", error.ParamName);
        Assert.Equal(unknown, error.ActualValue);
    }

    [Theory]
    [InlineData(TerrainKind.Settlement)]
    [InlineData(TerrainKind.OverheadGasPipeline)]
    [InlineData(TerrainKind.GasPipelineCrossingCandidate)]
    public async Task PassiveObjectsStayPermittedWithoutInventingTransitions(TerrainKind kind)
    {
        MapFeature feature = TestData.Polygon(1, kind, 10, 10, 30, 30);
        TerrainEvaluator evaluator = new([feature], new LocalProjection(TestData.P(0, 0)));
        EdgeEvaluation edge = Assert.IsType<EdgeEvaluation>(evaluator.Evaluate(new(0, 20), new(40, 20)));
        Assert.Equal(edge.Length, edge.Cost(CostProfile.Defaults[0]), 6);
        Assert.Empty(edge.BridgeCandidates);
        Assert.Empty(edge.GasCandidates);
        Assert.Empty(edge.Roads);
        await new GeodataPointPlacementValidator(new MemoryData(feature)).ValidateAsync(TestData.P(20, 20), default);
    }

    private sealed class ConfiguredRule(TerrainKind kind, bool blocks) : ITerrainRule
    {
        public TerrainKind Kind => kind;
        public bool Blocks => blocks;
        public void Apply(Geometry intersection, long featureId, TerrainContribution contribution) { }
    }
}
