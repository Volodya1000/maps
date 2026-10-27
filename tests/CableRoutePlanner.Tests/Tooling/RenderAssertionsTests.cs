using CableRoutePlanner.Windows7GuestAgent;
using Xunit;

namespace CableRoutePlanner.Tests.Tooling;

public sealed class RenderAssertionsTests
{
    [Fact]
    public void AcceptsDeterministicRedMarkerPixel()
    {
        Assert.True(MarkerPixel.IsMatch(220, 30, 30));
    }

    [Fact]
    public void RejectsEmptyOrUnrelatedPixel()
    {
        Assert.False(MarkerPixel.IsMatch(255, 255, 255));
        Assert.False(MarkerPixel.IsMatch(120, 30, 30));
    }
}
