using CableRoutePlanner.ViewModels;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class GeoCoordinateParserTests
{
    [Theory]
    [InlineData("54.5", "27.25", 54.5, 27.25)]
    [InlineData("-90", "-180", -90, -180)]
    [InlineData("90", "180", 90, 180)]
    public void ValidInputPreservesCoordinates(string latitude, string longitude, double expectedLatitude, double expectedLongitude)
    {
        Assert.True(GeoCoordinateParser.TryParse(latitude, longitude, out Domain.GeoCoordinate coordinate, out string error));
        Assert.Equal(expectedLatitude, coordinate.Latitude);
        Assert.Equal(expectedLongitude, coordinate.Longitude);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("", "27")]
    [InlineData("54,5", "27")]
    [InlineData("91", "27")]
    [InlineData("54", "181")]
    [InlineData("NaN", "27")]
    [InlineData("54", "Infinity")]
    public void InvalidInputReturnsActionableError(string latitude, string longitude)
    {
        Assert.False(GeoCoordinateParser.TryParse(latitude, longitude, out _, out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ChildVertexRetainsItsParentNumberAndBridgeExplanation()
    {
        var parent = new RouteVertexRow(1, default, RouteVertexKind.UserWaypoint, LegIndex: 1);
        var child = new RouteVertexRow(2, default, RouteVertexKind.Transition, LegIndex: 1, LegSubIndex: 1);
        Assert.False(parent.IsChild);
        Assert.True(child.IsChild);
        Assert.Equal("1", parent.DisplayLabel);
        Assert.Equal("1.1", child.DisplayLabel);
        Assert.NotEmpty(child.KindText);
        Assert.Empty(parent.KindText);
    }
}
