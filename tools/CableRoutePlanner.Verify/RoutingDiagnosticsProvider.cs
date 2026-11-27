using System.Text.Json.Serialization;
using CableRoutePlanner.Infrastructure.Routing;
using Microsoft.Extensions.Logging;

namespace CableRoutePlanner.Verify;

internal sealed record RoutingDiagnosticEvent(
    [property: JsonPropertyName("eventId")] int EventId,
    [property: JsonPropertyName("eventName")] string? EventName,
    [property: JsonPropertyName("fields")] IReadOnlyDictionary<string, object?> Fields);

internal sealed record RoutingDiagnosticsSnapshot(
    [property: JsonPropertyName("events")] IReadOnlyList<RoutingDiagnosticEvent> Events,
    [property: JsonPropertyName("droppedEvents")] int DroppedEvents);

/// <summary>Captures structured routing fields, without formatting or parsing localized log messages.</summary>
internal sealed class RoutingDiagnosticsProvider : ILoggerProvider
{
    private const int MaximumEvents = 1024;
    private readonly List<RoutingDiagnosticEvent> _events = [];
    private readonly object _gate = new();
    private int _droppedEvents;

    public ILogger CreateLogger(string categoryName) => new DiagnosticLogger(this,
        string.Equals(categoryName, typeof(GridRoutePlanner).FullName, StringComparison.Ordinal));

    public RoutingDiagnosticsSnapshot Capture()
    {
        lock (_gate)
        {
            return new(_events.ToArray(), _droppedEvents);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _events.Clear();
            _droppedEvents = 0;
        }
    }

    private void Record<TState>(EventId eventId, TState state)
    {
        if (eventId.Id is < 1 or > 9 || state is not IEnumerable<KeyValuePair<string, object?>> fields)
        {
            return;
        }
        Dictionary<string, object?> values = fields.Where(field => field.Key != "{OriginalFormat}")
            .ToDictionary(field => field.Key, field => field.Value is Enum reason ? reason.ToString() : field.Value,
                StringComparer.Ordinal);
        lock (_gate)
        {
            if (_events.Count < MaximumEvents)
            {
                _events.Add(new(eventId.Id, eventId.Name, values));
            }
            else
            {
                _droppedEvents++;
            }
        }
    }

    private sealed class DiagnosticLogger(RoutingDiagnosticsProvider owner, bool routingCategory) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => routingCategory && logLevel is >= LogLevel.Debug and < LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                owner.Record(eventId, state);
            }
        }
    }
}
