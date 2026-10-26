using CableRoutePlanner.Domain;
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
    public void InvalidCostsAreRejected(double cost) => Assert.Throws<ArgumentException>(() => new CostProfile("test", cost, 0, 0, 1).Validate());

    [Fact]
    public void CableReserveIsAppliedToMeasuredLength()
    {
        var m = new RouteMetrics(123, 0, 0, 0, 0, 0, 123, null);
        Assert.Equal(129.15, m.CableLengthMetres(0.05), 6);
        Assert.Null(m.AscentMetres);
        Assert.Throws<ArgumentOutOfRangeException>(() => m.CableLengthMetres(-0.1));
    }

    [Fact]
    public void GeographicBoundsContainInclusivePointsAndNestedBounds()
    {
        // Arrange
        var bounds = new GeoBounds(20, 10, 30, 15);
        var inside = new GeoCoordinate(12, 25);
        var edge = new GeoCoordinate(15, 30);
        var outside = new GeoCoordinate(15.1, 30);

        // Act
        var pointInside = bounds.Contains(inside);
        var pointOnEdge = bounds.Contains(edge);
        var pointOutside = bounds.Contains(outside);
        var nested = bounds.Contains(new GeoBounds(21, 11, 29, 14));
        var overlapping = bounds.Contains(new GeoBounds(19, 11, 29, 14));

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
        var error = Record.Exception(options.Validate);

        // Assert
        Assert.IsType<ArgumentException>(error);
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly System.Globalization.CultureInfo _previous = System.Globalization.CultureInfo.CurrentCulture;

        public CultureScope(string name) => System.Globalization.CultureInfo.CurrentCulture = new(name);

        public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = _previous;
    }
}
