using CableRoutePlanner.Windows7GuestAgent;
using System.Drawing;
using Xunit;

namespace CableRoutePlanner.Tests.Tooling;

public sealed class RenderAssertionsTests
{
    [Fact]
    public void AcceptsDeterministicRedMarkerPixel()
    {
        Assert.True(RenderAssertions.IsMarkerPixel(Color.FromArgb(220, 30, 30)));
    }

    [Fact]
    public void RejectsEmptyOrUnrelatedPixel()
    {
        Assert.False(RenderAssertions.IsMarkerPixel(Color.White));
        Assert.False(RenderAssertions.IsMarkerPixel(Color.FromArgb(120, 30, 30)));
    }
}
