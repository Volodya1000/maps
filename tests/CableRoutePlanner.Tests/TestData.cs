using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace CableRoutePlanner.Tests;

internal sealed class MemoryData(params MapFeature[] features) : IGeodataRepository
{
    public DataPackageInfo Info
    {
        get;
    } = new(
        "synthetic-test-only",
        new(27, 54, 27.006, 54.006),
        "Synthetic fixture",
        false,
        "tests only");

    public Task<IReadOnlyList<MapFeature>> QueryAsync(GeoBounds bounds, CancellationToken ct) => Task.FromResult<IReadOnlyList<MapFeature>>(features);
}

internal static class TestData
{
    public static GeoCoordinate P(double x, double y) => new(
        54 + y / 111195.08,
        27 + x / (111195.08 * Math.Cos(54 * Math.PI / 180)));

    public static MapFeature Polygon(
        long id,
        TerrainKind kind,
        double x1,
        double y1,
        double x2,
        double y2,
        string? approval = null)
    {
        Coordinate C(double x, double y)
        {
            var p = P(x, y);
            return new(p.Longitude, p.Latitude);
        }

        var geometry = new GeometryFactory().CreatePolygon([C(x1, y1), C(x2, y1), C(x2, y2), C(x1, y2), C(x1, y1)]);
        return new(id, kind, new WKBWriter().Write(geometry), "fixture", approval);
    }

    public static IRoutePlanner Planner(params MapFeature[] features) => new GridRoutePlanner(
        new MemoryData(features),
        NullLogger<GridRoutePlanner>.Instance);

    public static readonly RoutingOptions Options = new(20, 100000);

    public static string RealData => Path.Combine(AppContext.BaseDirectory, "Data");
}

internal sealed class MemorySettings : ISettingsStore
{
    public RoutingOptions Options { get; private set; } = TestData.Options;

    public RoutingOptions Load() => Options;

    public void Save(RoutingOptions options)
    {
        options.Validate();
        Options = options;
    }
}

internal sealed class MemoryClipboard : IClipboardService
{
    public string? Text
    {
        get; private set;
    }

    public Task SetTextAsync(string text)
    {
        Text = text;
        return Task.CompletedTask;
    }
}
