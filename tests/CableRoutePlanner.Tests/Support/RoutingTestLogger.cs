using CableRoutePlanner.Infrastructure.Routing;
using Microsoft.Extensions.Logging;

namespace CableRoutePlanner.Tests;

internal sealed record RoutingLogEntry(LogLevel Level, EventId Event, IReadOnlyDictionary<string, object?> Fields, Exception? Error);

internal sealed class RoutingTestLogger : ILogger<GridRoutePlanner>
{
    public List<RoutingLogEntry> Events { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> values)
        { throw new InvalidOperationException("Routing events must retain structured fields."); }
        Events.Add(new(logLevel, eventId, values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), exception));
    }
}
