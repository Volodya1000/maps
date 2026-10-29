using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.ViewModels;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class GasPipelineRoutingTests : ReactiveTestScope
{
    [Fact]
    public async Task OptInPrefersMappedGasCandidateWithoutClaimingEngineeringApproval()
    {
        MapFeature road = TestData.Line(1, TerrainKind.Road, (-100, 150), (700, 150));
        MapFeature candidate = TestData.Line(2, TerrainKind.GasPipelineCrossingCandidate,
            (160, 130), (160, 150), (160, 170));
        IRoutePlanner planner = TestData.Planner(road, candidate);
        GeoCoordinate[] points = [TestData.P(40, 100), TestData.P(280, 200)];
        IReadOnlyList<RouteAlternative> ordinary = await planner.PlanAsync(points, TestData.Options, CancellationToken.None);
        Assert.All(ordinary, route => Assert.Equal(0, route.Metrics.PotentialGasPipelineCrossings));
        IReadOnlyList<RouteAlternative> preferred = await planner.PlanAsync(points,
            TestData.Options with { PreferOverheadGasCrossings = true }, CancellationToken.None);
        RouteAlternative viaGas = Assert.Single(preferred, route => route.Metrics.PotentialGasPipelineCrossings > 0);
        Assert.Contains(viaGas.RoadContacts!, contact => contact.Geometry.Any(point => point.DistanceTo(TestData.P(160, 150)) < 1));
        Assert.Equal(0, viaGas.Metrics.ApprovedWaterCrossings);
        Assert.Equal(0, viaGas.Metrics.PotentialBridgeCrossings);
        Assert.True(viaGas.Metrics.RoadCrossings > 0);
    }

    [Fact]
    public void GasAlternativeHasAnExplicitCandidateBadge()
    {
        var store = new MemorySettings();
        using MapViewModel vm = TestData.CreateMapViewModel(new NavigationHost(),
            new PlanningSession(TestData.Planner(), TestData.AllowAllValidator),
            new MapAreaLoader(new MemoryData()), store, new MemoryClipboard());
        var route = new RouteAlternative(Guid.NewGuid(), "test", [TestData.P(20, 20), TestData.P(100, 100)],
            new RouteMetrics(100, 0, 0, 1, 0, 0, 100, null, PotentialGasPipelineCrossings: 1));
        vm.Alternatives.Add(route);
        vm.SelectedRoute = route;
        RouteChoice choice = Assert.Single(vm.RouteChoices);
        Assert.True(choice.HasGas);
        Assert.Equal("Газопроводы: 1", choice.GasText);
    }

    [Fact]
    public async Task CandidateOutsideLoadedAreaCannotBypassUnknownTerrain()
    {
        MapFeature water = TestData.Polygon(1, TerrainKind.River, 140, 0, 180, 1000);
        MapFeature candidate = TestData.Line(2, TerrainKind.GasPipelineCrossingCandidate,
            (100, -5), (160, -5), (220, -5));
        MapFeature road = TestData.Line(3, TerrainKind.Road, (160, -50), (160, 500));
        await Assert.ThrowsAsync<PlanningException>(() => TestData.Planner(water, candidate, road).PlanAsync(
            [TestData.P(40, 150), TestData.P(280, 150)],
            TestData.Options with { PreferOverheadGasCrossings = true }, CancellationToken.None));
    }

    [Theory]
    [InlineData(TerrainKind.River)]
    [InlineData(TerrainKind.Lake)]
    [InlineData(TerrainKind.Building)]
    public async Task GasCandidateCannotOpenBlockedTerrain(TerrainKind kind)
    {
        MapFeature obstacle = TestData.Polygon(1, kind, 140, -1000, 180, 1000);
        MapFeature candidate = TestData.Line(2, TerrainKind.GasPipelineCrossingCandidate,
            (100, 150), (160, 150), (220, 150));
        MapFeature road = TestData.Line(3, TerrainKind.Road, (160, -1000), (160, 1000));
        await Assert.ThrowsAsync<PlanningException>(() => TestData.Planner(obstacle, candidate, road).PlanAsync(
            [TestData.P(40, 150), TestData.P(280, 150)],
            TestData.Options with { PreferOverheadGasCrossings = true }, CancellationToken.None));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void GasCandidateCountMustBeConsistentWithRoadCrossings(int count)
    {
        var facts = new SegmentTerrainFacts(100, 0, 0, 0, 1, 0, 0, count);
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(facts.Validate);
        Assert.Equal(nameof(SegmentTerrainFacts.OverheadGasCandidates), error.ParamName);
        Assert.Equal(count, error.ActualValue);
    }

    [Fact]
    public void CandidatePreferenceChangesOnlyTheGroundCrossingSurcharge()
    {
        var facts = new SegmentTerrainFacts(100, 0, 0, 0, 1, 0, 0);
        var profile = new CostProfile("test", 0, 0, 500, 1);
        Assert.Equal(600, RouteCostCalculator.Calculate(facts, profile));
        Assert.Equal(100, RouteCostCalculator.Calculate(facts with { OverheadGasCandidates = 1 }, profile));
    }

    [Fact]
    public void PipelinePreferenceIsExplicitAndSurvivesSettingsReload()
    {
        var store = new MemorySettings();
        using var settings = new SettingsViewModel(new NavigationHost(), store);
        Assert.False(settings.PreferOverheadGasCrossings);
        settings.PreferOverheadGasCrossings = true;
        Assert.True(settings.CanSave);
        settings.Save.Execute().Subscribe();
        Assert.True(store.Load().PreferOverheadGasCrossings);
        settings.Reload();
        Assert.True(settings.PreferOverheadGasCrossings);
        Assert.False(settings.HasChanges);
    }
}
