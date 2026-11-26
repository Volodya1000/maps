using System.Text.Json;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Routing;
using CableRoutePlanner.Verify;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class VerifyDiagnosticsTests
{
    [Fact]
    public void DiagnosticCaptureBoundsMemoryAndReportsOmittedEvents()
    {
        using RoutingDiagnosticsProvider provider = new();
        ILogger logger = provider.CreateLogger(typeof(GridRoutePlanner).FullName!);
        for (int index = 0; index < 1100; index++)
        {
            RoutingLog.PreparationLog(logger, index, index);
        }

        RoutingDiagnosticsSnapshot snapshot = provider.Capture();

        Assert.InRange(snapshot.Events.Count, 1, 1024);
        Assert.Equal(1100 - snapshot.Events.Count, snapshot.DroppedEvents);
        Assert.True(snapshot.DroppedEvents > 0);
        Assert.Equal(0L, snapshot.Events[0].Fields["QueryMilliseconds"]);
        Assert.DoesNotContain(snapshot.Events, entry => entry.Fields.ContainsKey("{OriginalFormat}"));
    }

    [Theory]
    [InlineData(false, "success")]
    [InlineData(true, "no_route")]
    public async Task CrossingPilotPersistsSearchDiagnosticsForBothOutcomes(bool blocked, string expectedStatus)
    {
        MapFeature[] features = blocked
            ? [TestData.Polygon(1, TerrainKind.Building, 140, -5000, 180, 5000)] : [];
        using SqliteGeodataFixture fixture = new(features);
        using TemporaryDirectory output = new();
        string scenarioPath = Path.Combine(output.DirectoryPath, "scenario.json");
        string resultPath = Path.Combine(output.DirectoryPath, "result.json");
        File.WriteAllText(scenarioPath, JsonSerializer.Serialize(new
        {
            points = new[] { TestData.P(50, 150), TestData.P(300, 150) },
            routingOptions = TestData.Options,
            includeDiagnostics = true
        }));

        int exitCode = await CrossingPilot.RunAsync(fixture.DirectoryPath, scenarioPath, resultPath);

        Assert.Equal(0, exitCode);
        using JsonDocument result = JsonDocument.Parse(File.ReadAllText(resultPath));
        Assert.Equal(expectedStatus, result.RootElement.GetProperty("status").GetString());
        JsonElement diagnostics = result.RootElement.GetProperty("diagnostics");
        Assert.Equal(0, diagnostics.GetProperty("droppedEvents").GetInt32());
        JsonElement[] events = diagnostics.GetProperty("events").EnumerateArray().ToArray();
        Assert.Contains(events, entry => entry.GetProperty("eventId").GetInt32() == 5);
        Assert.Contains(events, entry => entry.GetProperty("eventId").GetInt32() == 6);
        Assert.Contains(events, entry => entry.GetProperty("eventId").GetInt32() == 7);
        Assert.Contains(events, entry => entry.GetProperty("eventId").GetInt32() == 8
            && entry.GetProperty("fields").GetProperty("Attempt").GetInt32() == 0);
        if (blocked)
        {
            Assert.Contains(events, entry => entry.GetProperty("eventId").GetInt32() == 9
                && !entry.GetProperty("fields").GetProperty("Found").GetBoolean());
            Assert.Contains(events, entry => entry.GetProperty("eventId").GetInt32() == 7
                && entry.GetProperty("fields").GetProperty("Reason").GetString() == "NoRouteInSearchArea");
        }
    }
}
