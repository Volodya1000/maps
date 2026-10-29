using CableRoutePlanner.Domain;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class GeoCoverageTests
{
    [Theory]
    [InlineData(50, 500, true)]
    [InlineData(300, 500, true)]
    [InlineData(175, 100, true)]
    [InlineData(175, 500, false)]
    [InlineData(100, 500, true)]
    [InlineData(100, 200, true)]
    [InlineData(-1, 100, false)]
    public void ConcaveCoverageIncludesEdgesAndExcludesMissingAreas(double x, double y, bool covered) =>
        Assert.Equal(covered, CoverageTestData.Concave().Contains(TestData.P(x, y)));

    [Fact]
    public void BoundaryIsAnImmutableSnapshot()
    {
        GeoCoordinate[] input = [TestData.P(0, 0), TestData.P(100, 0), TestData.P(100, 100), TestData.P(0, 0)];
        var coverage = new GeoCoverage(input);
        input[1] = TestData.P(50, 50);
        Assert.Equal(TestData.P(100, 0), coverage.Boundary[1]);
    }

    [Fact]
    public void InvalidRingsAreRejectedWithStructuredParameter()
    {
        GeoCoordinate[][] rings = [[], [TestData.P(0, 0), TestData.P(100, 0), TestData.P(100, 100)],
            [TestData.P(0, 0), TestData.P(100, 0), TestData.P(100, 100), TestData.P(0, 100)],
            [TestData.P(0, 0), TestData.P(50, 0), TestData.P(100, 0), TestData.P(0, 0)],
            [TestData.P(0, 0), TestData.P(0, 0), TestData.P(100, 100), TestData.P(0, 0)],
            [TestData.P(0, 0), TestData.P(100, 100), TestData.P(100, 0), TestData.P(0, 100), TestData.P(0, 0)]];
        foreach (GeoCoordinate[] ring in rings)
        {
            ArgumentException error = Assert.Throws<ArgumentException>(() => new GeoCoverage(ring));
            Assert.Equal("boundary", error.ParamName);
        }
        Assert.Throws<ArgumentNullException>(() => new GeoCoverage(null!));
    }
}
