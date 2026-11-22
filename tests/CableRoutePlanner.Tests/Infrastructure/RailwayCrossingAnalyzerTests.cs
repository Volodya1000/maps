using CableRoutePlanner.Infrastructure.Routing;
using NetTopologySuite.Geometries;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class RailwayCrossingAnalyzerTests
{
    [Fact]
    public void EmptyNetworkAndShortRoutesHaveNoCrossings()
    {
        RailwayCrossingAnalyzer empty = new([]);
        Assert.Equal(0, empty.Count([new(0, 0), new(10, 0)], default));
        RailwayCrossingAnalyzer analyzer = new([Line((5, -5), (5, 5))]);
        Assert.Equal(0, analyzer.Count([], default));
        Assert.Equal(0, analyzer.Count([new(0, 0)], default));
    }

    [Fact]
    public void CloseParallelTracksRemainSeparate()
    {
        RailwayCrossingAnalyzer analyzer = new([Line((5, -5), (5, 5)), Line((5.01, -5), (5.01, 5))]);
        Assert.Equal(2, analyzer.Count([new(0, 0), new(10, 0)], default));
    }

    [Fact]
    public void DuplicateMultiLineAndSplitWaysCountOnce()
    {
        MultiLineString tracks = new([Line((5, -5), (5, 0)), Line((5, 0), (5, 5))]);
        RailwayCrossingAnalyzer analyzer = new([tracks, Line((5, 4), (5, -4))]);
        Assert.Equal(1, analyzer.Count([new(0, 0), new(5, 0), new(10, 0)], default));
    }

    [Fact]
    public void JunctionAndCrossedTracksCountThroughRaysWithoutCountingBranchesTwice()
    {
        RailwayCrossingAnalyzer junction = new([Line((5, -5), (5, 0)), Line((5, 0), (4, 5)), Line((5, 0), (6, 5))]);
        Assert.Equal(1, junction.Count([new(0, 0), new(10, 0)], default));
        RailwayCrossingAnalyzer crossing = new([Line((3, -5), (7, 5)), Line((7, -5), (3, 5))]);
        Assert.Equal(2, crossing.Count([new(0, 0), new(10, 0)], default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedIncomingTrackAtForkCountsOnceRegardlessOfOsmSegmentation(bool split)
    {
        Geometry[] tracks = split
            ? [Line((0, -2), (0, 0)), Line((0, 0), (-1, 2)), Line((0, 0), (1, 2))]
            : [Line((0, -2), (0, 0), (-1, 2)), Line((0, -2), (0, 0), (1, 2))];
        RailwayCrossingAnalyzer analyzer = new(tracks);
        Assert.Equal(1, analyzer.Count([new(-2, 0), new(2, 0)], default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedSwitchRaysCountIndependentPassagesRegardlessOfSourceOrder(bool reverse)
    {
        Geometry[] tracks = [Line((-2, 2), (0, 0), (-2, -2)),
            Line((-2, 2), (0, 0), (2, -2)), Line((2, 2), (0, 0), (-2, -2))];
        RailwayCrossingAnalyzer analyzer = new(reverse ? tracks.Reverse() : tracks);
        Assert.Equal(2, analyzer.Count([new(-3, 0), new(3, 0)], default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedWholeAndSplitSwitchWaysPreserveCrossingCount(bool split)
    {
        List<Geometry> tracks = [Line((0, -2), (0, 0), (-1, 2)), Line((0, -2), (0, 0), (1, 2))];
        if (split)
        {
            tracks.Add(Line((1, -2), (0, 0)));
            tracks.Add(Line((0, 0), (-1, 2)));
        }
        else
        {
            tracks.Add(Line((1, -2), (0, 0), (-1, 2)));
        }
        RailwayCrossingAnalyzer analyzer = new(tracks);
        Assert.Equal(2, analyzer.Count([new(-2, 0), new(2, 0)], default));
    }

    [Fact]
    public void ClosedTrackSeamCountsAsInteriorCrossing()
    {
        RailwayCrossingAnalyzer analyzer = new([Line((0, 0), (10, 0), (10, 10), (0, 10), (0, 0))]);
        Assert.Equal(1, analyzer.Count([new(-1, -1), new(1, 1)], default));
    }

    [Fact]
    public void OpposingTangenciesAtOneNodeDoNotBecomeCrossings()
    {
        RailwayCrossingAnalyzer analyzer = new([Line((-1, 1), (0, 0), (1, 1)),
            Line((-1, -1), (0, 0), (1, -1))]);
        Assert.Equal(0, analyzer.Count([new(-2, 0), new(2, 0)], default));
    }

    [Fact]
    public void SharedSpurDoesNotHideSeparateThroughTrack()
    {
        RailwayCrossingAnalyzer analyzer = new([Line((0, -1), (0, 1)), Line((0, 0), (1, 0))]);
        Assert.Equal(1, analyzer.Count([new(-2, 0), new(2, 0)], default));
    }

    [Fact]
    public void ShortBentTrackTouchAndNearbyCrossingCountOnlyTheCrossing()
    {
        RailwayCrossingAnalyzer analyzer = new([Line((5, -5), (5, 0), (5.0001, -0.0001), (4, 5))]);
        Assert.Equal(1, analyzer.Count([new(0, 0), new(10, 0)], default));
    }

    [Fact]
    public void SharedSectionDoesNotCountOnEntryOrExit()
    {
        RailwayCrossingAnalyzer analyzer = new([Line((5, -5), (5, 5))]);
        Assert.Equal(0, analyzer.Count([new(0, -1), new(5, -1), new(5, 1), new(10, 1)], default));
    }

    [Fact]
    public void RepeatedVertexVisitsCountWithRouteMultiplicity()
    {
        RailwayCrossingAnalyzer analyzer = new([Line((5, -5), (5, 5))]);
        Assert.Equal(2, analyzer.Count([new(0, 0), new(5, 0), new(10, 0), new(5, 0), new(0, 0)], default));
    }

    [Fact]
    public void CancelledAnalysisDoesNotReturnPlausibleStatistics()
    {
        RailwayCrossingAnalyzer analyzer = new([Line((5, -5), (5, 5))]);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => analyzer.Count([new(0, 0), new(10, 0)], cancellation.Token));
    }

    private static LineString Line(params (double X, double Y)[] points) =>
        new(points.Select(point => new Coordinate(point.X, point.Y)).ToArray());
}
