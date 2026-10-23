using CableRoutePlanner.Domain;
using Xunit;
namespace CableRoutePlanner.Tests;
public sealed class DomainTests
{
    [Fact] public void GeodesicDistanceUsesRealUnits() => Assert.InRange(new GeoCoordinate(0,0).DistanceTo(new(0,1)),111194,111196);
    [Theory][InlineData(double.NaN,0)][InlineData(91,0)][InlineData(0,181)][InlineData(0,double.PositiveInfinity)]
    public void InvalidCoordinatesAreRejected(double latitude,double longitude) => Assert.Throws<ArgumentOutOfRangeException>(() => new GeoCoordinate(latitude,longitude));
    [Fact] public void CoordinatesAreFormattedInvariantly()
    {
        using var culture = new CultureScope("ru-RU");
        Assert.Equal("53.902284, 27.561831",new GeoCoordinate(53.902284,27.561831).ToString());
    }
    [Theory][InlineData(-1)][InlineData(double.NaN)][InlineData(double.PositiveInfinity)]
    public void InvalidCostsAreRejected(double cost) => Assert.Throws<ArgumentException>(() => new CostProfile("test",cost,0,0,1).Validate());
    [Fact] public void CableReserveIsAppliedToMeasuredLength()
    {
        var m = new RouteMetrics(123,0,0,0,0,123,null);
        Assert.Equal(129.15,m.CableLengthMetres(0.05),6); Assert.Null(m.AscentMetres);
        Assert.Throws<ArgumentOutOfRangeException>(() => m.CableLengthMetres(-0.1));
    }
    private sealed class CultureScope : IDisposable
    {
        private readonly System.Globalization.CultureInfo _previous = System.Globalization.CultureInfo.CurrentCulture;
        public CultureScope(string name) => System.Globalization.CultureInfo.CurrentCulture = new(name);
        public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = _previous;
    }
}
