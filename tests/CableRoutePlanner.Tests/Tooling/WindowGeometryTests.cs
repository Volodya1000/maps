using CableRoutePlanner.Windows7GuestAgent;
using Xunit;

namespace CableRoutePlanner.Tests.Tooling;

public sealed class WindowGeometryTests
{
    [Fact]
    public void AcceptsWindowWithNegativeScreenCoordinates()
    {
        Win32Rect rectangle = new(-1280, -20, -480, 580);

        Assert.True(WindowGeometry.IsUsable(rectangle));
        Assert.Equal(800, rectangle.Width);
        Assert.Equal(600, rectangle.Height);
    }

    [Theory]
    [InlineData(640, 480, true)]
    [InlineData(639, 480, false)]
    [InlineData(640, 479, false)]
    [InlineData(0, 600, false)]
    public void RejectsInvalidOrTooSmallWindow(int width, int height, bool expected)
    {
        Win32Rect rectangle = new(10, 20, 10 + width, 20 + height);

        Assert.Equal(expected, WindowGeometry.IsUsable(rectangle));
    }

    [Fact]
    public void RejectsInvertedWin32Rect()
    {
        Assert.False(WindowGeometry.IsUsable(new Win32Rect(100, 100, 50, 50)));
    }
}
