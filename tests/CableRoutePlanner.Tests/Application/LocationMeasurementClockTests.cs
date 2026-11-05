using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class LocationMeasurementClockTests
{
    [Theory]
    [InlineData(-3600)]
    [InlineData(0)]
    [InlineData(3600)]
    public void MonotonicFixTimeAllowsTrackingWithAnyDeviceClockOffset(int clockOffsetSeconds)
    {
        DateTimeOffset now = new DateTimeOffset(2026, 11, 5, 15, 39, 32, TimeSpan.Zero)
            .AddSeconds(clockOffsetSeconds);
        GeoCoordinate start = new(53.950121, 27.725782);
        GeoCoordinate end = new(53.950005, 27.726929);
        RouteProgressTracker tracker = new();
        tracker.Begin(new RouteAlternative(Guid.NewGuid(), "Маршрут из записи", [start, end],
            new RouteMetrics(76, 0, 76, 0, 0, 0, 76, null)));
        tracker.Resume();
        for (int step = 0; step <= 40; step++)
        {
            DateTimeOffset received = now.AddSeconds(step);
            DateTimeOffset timestamp = LocationMeasurementClock.GetTimestamp(received,
                TimeSpan.FromSeconds(10_000 + step), TimeSpan.FromSeconds(9_999.9 + step));
            GeoCoordinate coordinate = new(start.Latitude + (end.Latitude - start.Latitude) * step / 40,
                start.Longitude + (end.Longitude - start.Longitude) * step / 40);
            RouteProgressState state = tracker.Accept(new LocationMeasurement(coordinate, 5, timestamp, 1.9), received);
            if (step == 40)
            {
                Assert.InRange(state.CoveragePercent, 95, 100);
                Assert.Equal(LocationQuality.Good, state.LocationQuality);
            }
        }
    }

    [Fact]
    public void OldFixRetainsItsAgeAndCannotIncreaseProgress()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset timestamp = LocationMeasurementClock.GetTimestamp(now,
            TimeSpan.FromHours(2), TimeSpan.FromHours(2).Subtract(TimeSpan.FromMinutes(5)));

        Assert.Equal(TimeSpan.FromMinutes(5), now - timestamp);
        RouteProgressTracker tracker = new();
        GeoCoordinate start = new(53.950121, 27.725782);
        GeoCoordinate end = new(53.950005, 27.726929);
        tracker.Begin(new RouteAlternative(Guid.NewGuid(), "Тест", [start, end],
            new RouteMetrics(76, 0, 76, 0, 0, 0, 76, null)));
        tracker.Resume();
        tracker.Accept(new LocationMeasurement(start, 5, now), now);
        RouteProgressState state = tracker.Accept(new LocationMeasurement(end, 5, timestamp), now);

        Assert.Equal(0, state.CoveredDistanceMetres);
        Assert.Equal(ProgressRejectionReason.StaleTimestamp, state.RejectionReason);
    }
}
