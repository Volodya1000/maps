namespace CableRoutePlanner.Application;

// Converts the age supplied by a monotonic platform clock into the same UTC
// time base used by the tracker. Satellite UTC and the device clock can differ.
public static class LocationMeasurementClock
{
    public static DateTimeOffset GetTimestamp(
        DateTimeOffset receivedUtc,
        TimeSpan receivedElapsedTime,
        TimeSpan measuredElapsedTime) =>
        receivedUtc - (receivedElapsedTime - measuredElapsedTime);
}
