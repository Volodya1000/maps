using CableRoutePlanner.Domain;
using CableRoutePlanner.Application;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class DomainTests
{
    [Fact]
    public void GeodesicDistanceUsesRealUnits() => Assert.InRange(
        new GeoCoordinate(0, 0).DistanceTo(new(0, 1)),
        111194,
        111196);

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(91, 0)]
    [InlineData(0, 181)]
    [InlineData(0, double.PositiveInfinity)]
    public void InvalidCoordinatesAreRejected(double latitude, double longitude) => Assert.Throws<ArgumentOutOfRangeException>(() => new GeoCoordinate(latitude, longitude));

    [Fact]
    public void CoordinatesAreFormattedInvariantly()
    {
        using var culture = new CultureScope("ru-RU");
        Assert.Equal("53.902284, 27.561831", new GeoCoordinate(53.902284, 27.561831).ToString());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidCostsAreRejected(double cost) => Assert.Throws<ArgumentOutOfRangeException>(() => new CostProfile("test", cost, 0, 0, 1).Validate());

    [Theory]
    [InlineData(nameof(CostProfile.Forest), -1d)]
    [InlineData(nameof(CostProfile.Field), double.NaN)]
    [InlineData(nameof(CostProfile.RoadCrossingMetres), double.PositiveInfinity)]
    [InlineData(nameof(CostProfile.Clearing), 0d)]
    [InlineData(nameof(CostProfile.Clearing), 1.1d)]
    public void InvalidCostProfileReportsItsOwnParameter(string parameter, double value)
    {
        var profile = new CostProfile("test",
            parameter == nameof(CostProfile.Forest) ? value : 0,
            parameter == nameof(CostProfile.Field) ? value : 0,
            parameter == nameof(CostProfile.RoadCrossingMetres) ? value : 0,
            parameter == nameof(CostProfile.Clearing) ? value : 1);

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(profile.Validate);

        Assert.Equal(parameter, error.ParamName);
        Assert.Equal(value, error.ActualValue);
    }

    [Theory]
    [InlineData("Короче", 1d)]
    [InlineData("Баланс", 0.65d)]
    [InlineData("Проще прокладка", 0.5d)]
    public void EachCostProfileProvidesAnAdmissibleMinimumCost(string name, double minimumCostPerMetre) =>
        Assert.Equal(minimumCostPerMetre, Assert.Single(CostProfile.Defaults, profile => profile.Name == name).MinimumCostPerMetre);

    [Fact]
    public void CableReserveIsAppliedToMeasuredLength()
    {
        var m = new RouteMetrics(123, 0, 0, 0, 0, 0, 123, null);
        Assert.Equal(129.15, m.CableLengthMetres(0.05), 6);
        Assert.Null(m.AscentMetres);
        Assert.Throws<ArgumentOutOfRangeException>(() => m.CableLengthMetres(-0.1));
    }

    [Theory]
    [InlineData(TerrainKind.Forest, TerrainTraversalRule.Costed)]
    [InlineData(TerrainKind.Field, TerrainTraversalRule.Costed)]
    [InlineData(TerrainKind.Clearing, TerrainTraversalRule.Costed)]
    [InlineData(TerrainKind.Marsh, TerrainTraversalRule.Blocked)]
    [InlineData(TerrainKind.Lake, TerrainTraversalRule.Blocked)]
    [InlineData(TerrainKind.River, TerrainTraversalRule.Blocked)]
    [InlineData(TerrainKind.Building, TerrainTraversalRule.Blocked)]
    [InlineData(TerrainKind.Road, TerrainTraversalRule.CrossingPenalty)]
    [InlineData(TerrainKind.BridgeCandidate, TerrainTraversalRule.ConditionalTransition)]
    [InlineData(TerrainKind.ApprovedCrossing, TerrainTraversalRule.ApprovedTransition)]
    public void TraversalPolicyDefinesSubjectRules(TerrainKind terrain, TerrainTraversalRule expected) =>
        Assert.Equal(expected, TerrainTraversalPolicy.For(terrain));

    [Fact]
    public void SegmentCostMatchesEachConfiguredProfile()
    {
        var facts = new SegmentTerrainFacts(100, 20, 10, 5, 2, 1, 1);
        foreach (CostProfile profile in CostProfile.Defaults)
        {
            double expected = 100 - 5 * (1 - profile.Clearing)
                + 20 * profile.Forest + 10 * profile.Field
                + 3 * profile.RoadCrossingMetres;
            Assert.Equal(expected, RouteCostCalculator.Calculate(facts, profile), 8);
        }
    }

    [Theory]
    [InlineData(nameof(SegmentTerrainFacts.LengthMetres), -1d)]
    [InlineData(nameof(SegmentTerrainFacts.LengthMetres), double.NaN)]
    [InlineData(nameof(SegmentTerrainFacts.LengthMetres), double.PositiveInfinity)]
    [InlineData(nameof(SegmentTerrainFacts.LengthMetres), double.NegativeInfinity)]
    [InlineData(nameof(SegmentTerrainFacts.ForestMetres), -1d)]
    [InlineData(nameof(SegmentTerrainFacts.FieldMetres), double.NaN)]
    [InlineData(nameof(SegmentTerrainFacts.ClearingMetres), double.PositiveInfinity)]
    public void SegmentFactsReportInvalidDistanceParameterAndValue(string parameter, double value)
    {
        SegmentTerrainFacts facts = parameter switch
        {
            nameof(SegmentTerrainFacts.LengthMetres) => new SegmentTerrainFacts(value, 0, 0, 0, 0, 0, 0),
            nameof(SegmentTerrainFacts.ForestMetres) => new SegmentTerrainFacts(0, value, 0, 0, 0, 0, 0),
            nameof(SegmentTerrainFacts.FieldMetres) => new SegmentTerrainFacts(0, 0, value, 0, 0, 0, 0),
            _ => new SegmentTerrainFacts(0, 0, 0, value, 0, 0, 0)
        };

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(facts.Validate);

        Assert.Equal(parameter, error.ParamName);
        Assert.Equal(value, error.ActualValue);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Theory]
    [InlineData(nameof(SegmentTerrainFacts.RoadCrossings))]
    [InlineData(nameof(SegmentTerrainFacts.BridgeCandidates))]
    [InlineData(nameof(SegmentTerrainFacts.ApprovedCrossings))]
    public void SegmentFactsReportInvalidCountParameterAndValue(string parameter)
    {
        SegmentTerrainFacts facts = parameter switch
        {
            nameof(SegmentTerrainFacts.RoadCrossings) => new SegmentTerrainFacts(0, 0, 0, 0, -2, 0, 0),
            nameof(SegmentTerrainFacts.BridgeCandidates) => new SegmentTerrainFacts(0, 0, 0, 0, 0, -2, 0),
            _ => new SegmentTerrainFacts(0, 0, 0, 0, 0, 0, -2)
        };

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(facts.Validate);

        Assert.Equal(parameter, error.ParamName);
        Assert.Equal(-2, error.ActualValue);
    }

    [Fact]
    public void SegmentFactsAllowZeroAndOverlappingTerrainMeasurements()
    {
        new SegmentTerrainFacts(0, 0, 0, 0, 0, 0, 0).Validate();
        // Terrain layers can overlap; measured contributions are not necessarily a partition of length.
        new SegmentTerrainFacts(10, 10, 8, 10, 0, 0, 0).Validate();
    }

    [Theory]
    [InlineData(11, 0, 0, nameof(SegmentTerrainFacts.ForestMetres))]
    [InlineData(0, 11, 0, nameof(SegmentTerrainFacts.FieldMetres))]
    [InlineData(0, 0, 11, nameof(SegmentTerrainFacts.ClearingMetres))]
    public void TerrainContributionCannotExceedSegment(double forest, double field, double clearing, string parameter)
    {
        var facts = new SegmentTerrainFacts(10, forest, field, clearing, 0, 0, 0);
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(facts.Validate);
        Assert.Equal(parameter, error.ParamName);
        Assert.Equal(11d, error.ActualValue);
    }

    [Fact]
    public void ExtremeFiniteInputsCannotProduceInfiniteCost()
    {
        var facts = new SegmentTerrainFacts(double.MaxValue, double.MaxValue, 0, 0, 0, 0, 0);
        var profile = new CostProfile("extreme", double.MaxValue, 0, 0, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => RouteCostCalculator.Calculate(facts, profile));
    }

    [Fact]
    public void AlternativesUseNamedCorridorPolicyThreshold() =>
        Assert.Equal(0.90, AlternativeRoutePolicy.MaximumCorridorOverlap);

    [Fact]
    public void GeographicBoundsContainInclusivePointsAndNestedBounds()
    {
        // Arrange
        var bounds = new GeoBounds(20, 10, 30, 15);
        var inside = new GeoCoordinate(12, 25);
        var edge = new GeoCoordinate(15, 30);
        var outside = new GeoCoordinate(15.1, 30);

        // Act
        bool pointInside = bounds.Contains(inside);
        bool pointOnEdge = bounds.Contains(edge);
        bool pointOutside = bounds.Contains(outside);
        bool nested = bounds.Contains(new GeoBounds(21, 11, 29, 14));
        bool overlapping = bounds.Contains(new GeoBounds(19, 11, 29, 14));

        // Assert
        Assert.True(pointInside);
        Assert.True(pointOnEdge);
        Assert.False(pointOutside);
        Assert.True(nested);
        Assert.False(overlapping);
    }

    [Theory]
    [InlineData(4, 100, 0)]
    [InlineData(201, 100, 0)]
    [InlineData(25, 99, 0)]
    [InlineData(25, 1000001, 0)]
    [InlineData(25, 100, -0.1)]
    [InlineData(25, 100, 1.1)]
    public void RoutingOptionsRejectEveryOutOfRangeSetting(double cellSize, int maxNodes, double reserve)
    {
        // Arrange
        var options = new RoutingOptions(cellSize, maxNodes, reserve);

        // Act
        Exception error = Record.Exception(options.Validate);

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_000_001)]
    public void RoutingOptionsRejectOutOfRangeMaximumRouteLength(double maximum)
    {
        RoutingOptions options = new(MaxRouteLengthMetres: maximum);

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly System.Globalization.CultureInfo _previous = System.Globalization.CultureInfo.CurrentCulture;

        public CultureScope(string name) => System.Globalization.CultureInfo.CurrentCulture = new(name);

        public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = _previous;
    }
}
