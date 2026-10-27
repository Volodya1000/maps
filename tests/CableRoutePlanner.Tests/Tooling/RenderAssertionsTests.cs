using CableRoutePlanner.Windows7GuestAgent;
using System.Drawing;
using Xunit;

namespace CableRoutePlanner.Tests.Tooling;

public sealed class RenderAssertionsTests
{
    [Fact]
    public void AcceptsDeterministicRedMarker()
    {
        using Bitmap bitmap = new(20, 20);
        using Graphics graphics = Graphics.FromImage(bitmap);
        using SolidBrush brush = new(Color.FromArgb(220, 30, 30));
        graphics.FillRectangle(brush, 5, 5, 10, 10);

        Assert.True(RenderAssertions.ContainsRedMarker(bitmap));
    }

    [Fact]
    public void RejectsEmptyOrUnrelatedScreenshot()
    {
        using Bitmap bitmap = new(20, 20);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);

        Assert.False(RenderAssertions.ContainsRedMarker(bitmap));
    }
}
