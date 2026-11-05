using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class RouteProgressTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TwoPointRouteCountsOnlyTheCredibleIntervalBetweenFixes()
    {
        RouteProgressTracker tracker = StartTracker([Metres(0), Metres(1_000)]);

        tracker.Accept(Fix(100, 1, Start.AddSeconds(1)), Start.AddSeconds(1));
        RouteProgressState state = tracker.Accept(Fix(350, 2, Start.AddSeconds(20)), Start.AddSeconds(20));

        Assert.Equal(250, state.CoveredDistanceMetres, 0);
        Assert.Equal(25, state.CoveragePercent, 1);
    }

    [Fact]
    public void MultipleSegmentsPreserveTheRouteDistanceAcrossVertices()
    {
        RouteProgressTracker tracker = StartTracker([Metres(0), Metres(500), Metres(1_000)]);

        tracker.Accept(Fix(100, 1, Start.AddSeconds(1)), Start.AddSeconds(1));
        RouteProgressState state = tracker.Accept(Fix(900, 1, Start.AddSeconds(40)), Start.AddSeconds(40));

        Assert.Equal(800, state.CoveredDistanceMetres, 2);
        Assert.Equal(80, state.CoveragePercent, 1);
    }

    [Fact]
    public void FullRouteReachesOneHundredPercent()
    {
        RouteProgressTracker tracker = StartTracker([Metres(0), Metres(1_000)]);

        tracker.Accept(Fix(0, 1, Start.AddSeconds(1)), Start.AddSeconds(1));
        RouteProgressState state = tracker.Accept(Fix(1_000, 1, Start.AddSeconds(40)), Start.AddSeconds(40));

        Assert.Equal(1_000, state.CoveredDistanceMetres, 2);
        Assert.Equal(100, state.CoveragePercent, 1);
    }

    [Fact]
    public void KeyCaseCountsFirstAndLastIntervalsButNotTheUnobservedMiddle()
    {
        RouteProgressTracker tracker = StartTracker([Metres(0), Metres(1_000)]);

        tracker.Accept(Fix(0, 1, Start.AddSeconds(1)), Start.AddSeconds(1));
        tracker.Accept(Fix(200, 1, Start.AddSeconds(20)), Start.AddSeconds(20));
        tracker.Accept(Fix(700, 1, Start.AddMinutes(5)), Start.AddMinutes(5));
        RouteProgressState state = tracker.Accept(Fix(1_000, 1, Start.AddMinutes(5).AddSeconds(30)), Start.AddMinutes(5).AddSeconds(30));

        Assert.Equal(500, state.CoveredDistanceMetres, 2);
        Assert.Equal(50, state.CoveragePercent, 1);
        Assert.Equal(2, state.CoveredIntervals.Count);
    }

    [Fact]
    public void StartingInTheMiddleCountsMovementFromTheFirstFix()
    {
        RouteProgressTracker tracker = StartTracker([Metres(0), Metres(1_000)]);

        tracker.Accept(Fix(500, 1, Start.AddSeconds(1)), Start.AddSeconds(1));
        RouteProgressState state = tracker.Accept(Fix(700, 1, Start.AddSeconds(30)), Start.AddSeconds(30));

        Assert.Equal(200, state.CoveredDistanceMetres, 2);
    }

    [Fact]
    public void ReverseMovementAndRepeatedPassesDoNotDoubleCount()
    {
        RouteProgressTracker tracker = StartTracker([Metres(0), Metres(1_000)]);

        tracker.Accept(Fix(900, 1, Start.AddSeconds(1)), Start.AddSeconds(1));
        tracker.Accept(Fix(500, 1, Start.AddSeconds(30)), Start.AddSeconds(30));
        tracker.Accept(Fix(100, 1, Start.AddSeconds(60)), Start.AddSeconds(60));
        RouteProgressState state = tracker.Accept(Fix(900, 1, Start.AddSeconds(90)), Start.AddSeconds(90));

        Assert.Equal(800, state.CoveredDistanceMetres, 2);
        Assert.Single(state.CoveredIntervals);
        Assert.Equal(100, state.CoveredIntervals[0].StartMetres, 2);
        Assert.Equal(900, state.CoveredIntervals[0].EndMetres, 2);
    }

    [Fact]
    public void ClosedRouteAndSelfIntersectionDoNotCreateInvalidProgress()
    {
        RouteProgressTracker tracker = StartTracker(
        [
            Metres(0, 0), Metres(100, 100), Metres(0, 200), Metres(0, 0)
        ]);

        tracker.Accept(Fix(0, 1, Start.AddSeconds(1), y: 1), Start.AddSeconds(1));
        RouteProgressState state = tracker.Accept(Fix(100, 1, Start.AddSeconds(20), y: 100), Start.AddSeconds(20));

        Assert.InRange(state.CoveredDistanceMetres, 0, state.TotalDistanceMetres);
        Assert.All(state.CoveredIntervals, interval =>
        {
            Assert.InRange(interval.StartMetres, 0, state.TotalDistanceMetres);
            Assert.InRange(interval.EndMetres, 0, state.TotalDistanceMetres);
        });
    }

    [Fact]
    public void DuplicateSegmentsAreIgnoredForDistanceButKeepTrackingAlive()
    {
        RouteProgressTracker tracker = StartTracker([Metres(0), Metres(0), Metres(1_000), Metres(1_000)]);

        tracker.Accept(Fix(100, 1, Start.AddSeconds(1)), Start.AddSeconds(1));
        RouteProgressState state = tracker.Accept(Fix(400, 1, Start.AddSeconds(30)), Start.AddSeconds(30));

        Assert.Equal(300, state.CoveredDistanceMetres, 2);
    }

    [Fact]
    public void InvalidMeasurementsDoNotIncreaseProgress()
    {
        RouteProgressTracker tracker = StartTracker([Metres(0), Metres(1_000)]);

        tracker.Accept(Fix(0, 1, Start.AddSeconds(1)), Start.AddSeconds(1));
        tracker.Accept(Fix(200, 1, Start.AddSeconds(20)), Start.AddSeconds(20));
        RouteProgressState invalid = tracker.Accept(
            Fix(300, 1, Start.AddSeconds(30), speed: 100), Start.AddSeconds(30));
        Assert.Equal(LocationQuality.Invalid, invalid.LocationQuality);
        RouteProgressState state = tracker.Accept(Fix(400, 1, Start.AddSeconds(40)), Start.AddSeconds(40));

        Assert.Equal(200, state.CoveredDistanceMetres, 2);
    }

    [Fact]
    public void StaleAndOutOfOrderMeasurementsAreRejected()
    {
        RouteProgressTracker tracker = StartTracker([Metres(0), Metres(1_000)]);

        tracker.Accept(Fix(0, 1, Start.AddSeconds(20)), Start.AddSeconds(20));
        tracker.Accept(Fix(200, 1, Start.AddMinutes(-10)), Start.AddMinutes(-10));
        RouteProgressState state = tracker.Accept(Fix(300, 1, Start.AddSeconds(10)), Start.AddSeconds(10));

        Assert.Equal(0, state.CoveredDistanceMetres, 2);
    }

    [Fact]
    public void PauseAndResumeRequireAFreshContinuityPair()
    {
        RouteProgressTracker tracker = StartTracker([Metres(0), Metres(1_000)]);

        tracker.Accept(Fix(0, 1, Start.AddSeconds(1)), Start.AddSeconds(1));
        tracker.Accept(Fix(200, 1, Start.AddSeconds(20)), Start.AddSeconds(20));
        tracker.Pause();
        tracker.Resume();
        tracker.Accept(Fix(800, 1, Start.AddMinutes(5)), Start.AddMinutes(5));
        RouteProgressState state = tracker.Accept(Fix(1_000, 1, Start.AddMinutes(5).AddSeconds(30)), Start.AddMinutes(5).AddSeconds(30));

        Assert.Equal(400, state.CoveredDistanceMetres, 2);
        Assert.Equal(2, state.CoveredIntervals.Count);
    }

    [Fact]
    public void CoveredGeometryContainsOnlyTheOriginalRouteSegments()
    {
        RouteProgressTracker tracker = StartTracker([Metres(0), Metres(500), Metres(1_000)]);

        tracker.Accept(Fix(100, 1, Start.AddSeconds(1)), Start.AddSeconds(1));
        RouteProgressState state = tracker.Accept(Fix(600, 1, Start.AddSeconds(40)), Start.AddSeconds(40));

        Assert.Single(state.CoveredGeometry);
        Assert.InRange(state.CoveredGeometry[0][0].DistanceTo(Metres(100)), 0, 0.01);
        Assert.InRange(state.CoveredGeometry[0][^1].DistanceTo(Metres(600)), 0, 0.01);
    }

    [Fact]
    public void ParallelNeighboringSegmentsDoNotTeleportProgressToTheOtherLeg()
    {
        IReadOnlyList<GeoCoordinate> geometry =
        [Metres(0), Metres(1_000), Metres(1_000, 20), Metres(0, 20)];
        RouteProgressTracker tracker = StartTracker(geometry);

        tracker.Accept(Fix(500, 1, Start.AddSeconds(1)), Start.AddSeconds(1));
        RouteProgressState state = tracker.Accept(
            Fix(500, 1, Start.AddSeconds(20), y: 20), Start.AddSeconds(20));

        Assert.InRange(state.CoveredDistanceMetres, 0, 1);
    }

    [Fact]
    public void ParallelNeighboringSegmentsDoNotTeleportAfterLongSignalGap()
    {
        IReadOnlyList<GeoCoordinate> geometry =
        [Metres(0), Metres(1_000), Metres(1_000, 20), Metres(0, 20)];
        RouteProgressTracker tracker = StartTracker(geometry);

        tracker.Accept(Fix(500, 1, Start.AddSeconds(1)), Start.AddSeconds(1));
        RouteProgressState state = tracker.Accept(
            Fix(500, 1, Start.AddMinutes(2), y: 20), Start.AddMinutes(2));

        Assert.InRange(state.CoveredDistanceMetres, 0, 1);
    }

    [Fact]
    public void SnapshotRestoresOnlyTheSameGeometry()
    {
        IReadOnlyList<GeoCoordinate> geometry = [Metres(0), Metres(1_000)];
        RouteAlternative route = CreateRoute(geometry);
        RouteProgressTracker tracker = new();
        tracker.Begin(route);
        tracker.Resume();
        tracker.Accept(Fix(0, 1, Start.AddSeconds(1)), Start.AddSeconds(1));
        tracker.Accept(Fix(400, 1, Start.AddSeconds(30)), Start.AddSeconds(30));
        RouteProgressSnapshot snapshot = tracker.CreateSnapshot(Guid.NewGuid(), Start, Start.AddSeconds(30));

        RouteProgressTracker restored = new();
        RouteProgressState restoredState = restored.Restore(snapshot, route);
        RouteProgressState rejected = restored.Restore(snapshot, CreateRoute([Metres(0), Metres(900)]));

        Assert.Equal(400, restoredState.CoveredDistanceMetres, 2);
        Assert.Equal(0, rejected.CoveredDistanceMetres, 2);
    }

    private static RouteProgressTracker StartTracker(IReadOnlyList<GeoCoordinate> geometry)
    {
        RouteProgressTracker tracker = new(new RouteProgressConfiguration(
            MaxAccuracyMetres: 50,
            MaxMeasurementAge: TimeSpan.FromMinutes(2),
            MaxConsecutiveGap: TimeSpan.FromMinutes(2),
            MaxSpeedMetresPerSecond: 30,
            ProjectionToleranceMetres: 60,
            StationaryDistanceMetres: 3));
        tracker.Begin(CreateRoute(geometry));
        tracker.Resume();
        return tracker;
    }

    private static RouteAlternative CreateRoute(IReadOnlyList<GeoCoordinate> geometry) => new(
        Guid.NewGuid(),
        "Тестовый маршрут",
        geometry,
        new RouteMetrics(geometry.Zip(geometry.Skip(1), (a, b) => a.DistanceTo(b)).Sum(), 0, 0, 0, 0, 0, 0, null));

    private static LocationMeasurement Fix(
        double metres,
        double accuracy,
        DateTimeOffset timestamp,
        double y = 0,
        double? speed = null) =>
        new(Metres(metres, y), accuracy, timestamp, speed, false);

    private static GeoCoordinate Metres(double x, double y = 0)
    {
        const double metresPerDegree = 111_195.08;
        return new(54 + y / metresPerDegree, 27 + x / (metresPerDegree * Math.Cos(54 * Math.PI / 180)));
    }
}

public sealed class HeadingSmootherTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void CardinalHeadingsAreNormalized(double heading)
    {
        HeadingSmoother smoother = new(1);

        Assert.Equal(heading, smoother.Update(heading, true)!.Value, 6);
    }

    [Fact]
    public void HeadingWrapAroundUsesTheShortestTurn()
    {
        HeadingSmoother smoother = new(0.5);

        smoother.Update(359, true);
        double result = smoother.Update(1, true)!.Value;

        Assert.True(Math.Min(result, 360 - result) <= 1);
    }

    [Fact]
    public void MapAndDisplayRotationAreAppliedWithoutChangingGpsHeading()
    {
        Assert.Equal(20, HeadingSmoother.ToMapBearing(90, 45, 25), 6);
    }

    [Fact]
    public void UnreliableHeadingIsHiddenAndDoesNotThrow()
    {
        HeadingSmoother smoother = new(0.25);

        Assert.Null(smoother.Update(90, false));
    }

    [Fact]
    public void ResetStartsAHeadingSessionWithoutThePreviousFilterState()
    {
        HeadingSmoother smoother = new(0.25);

        smoother.Update(0, true);
        smoother.Update(90, true);
        smoother.Reset();

        Assert.Equal(180, smoother.Update(180, true));
    }
}
