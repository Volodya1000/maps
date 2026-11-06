using CableRoutePlanner.ViewModels;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class MapViewportStateTests
{
    [Fact]
    public void InvalidViewportValuesAreRejected()
    {
        Assert.False(MapViewportState.TryCreate(double.NaN, 10, 1, 0, out _));
        Assert.False(MapViewportState.TryCreate(10, 10, 0, 0, out _));
        Assert.False(MapViewportState.TryCreate(10, 10, 1, double.PositiveInfinity, out _));
        Assert.True(MapViewportState.TryCreate(10, 20, 3, 450, out MapViewportState? state));
        Assert.Equal(90, state!.Rotation);
    }
}
