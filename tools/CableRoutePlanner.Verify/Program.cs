using System.Globalization;
using System.Text.Json;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure;
using CableRoutePlanner.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

static ServiceProvider CreateServices(string dataDirectory, string settingsPath)
{
    var services = new ServiceCollection();
    services.AddLogging(logging => logging.AddConsole());
    services.AddInfrastructure();
    services.AddPersistence(dataDirectory, settingsPath);
    services.AddSingleton<IPlanningSession, PlanningSession>();
    return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
}

if (args.Length == 3 && args[0] == "--inspect")
{
    using var provider = CreateServices(args[1], Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
    await CableRoutePlanner.Verify.PackageInspection.RunAsync(
        provider.GetRequiredService<IGeodataRepository>(), args[2]);
    return 0;
}

if (args.Length < 3)
{
    Console.Error.WriteLine(
        "Usage: CableRoutePlanner.Verify <data-directory> <output.geojson> <lat,lon> <lat,lon> [...]");
    return 2;
}

try
{
    var points = args.Skip(2).Select(value =>
    {
        var parts = value.Split(',');
        if (parts.Length != 2)
        {
            throw new ArgumentException("Coordinate format: latitude,longitude");
        }

        return new GeoCoordinate(
            double.Parse(parts[0], CultureInfo.InvariantCulture),
            double.Parse(parts[1], CultureInfo.InvariantCulture));
    }).ToArray();
    using var services = CreateServices(
        args[0],
        Path.Combine(Path.GetTempPath(), "CableRoutePlanner.Verify.settings.json"));
    var session = services.GetRequiredService<IPlanningSession>();
    foreach (var point in points)
    {
        session.Add(point);
    }

    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancel.Cancel();
    };
    var routes = await session.BuildAsync(new RoutingOptions(), cancel.Token);
    var features = routes.Select(
        r => new { type = "Feature", id = r.Id, properties = new { name = r.Name, metrics = r.Metrics }, geometry = new { type = "LineString", coordinates = r.Geometry.Select(p => new[] { p.Longitude, p.Latitude }) } });
    File.WriteAllText(
        args[1],
        JsonSerializer.Serialize(
            new
            {
                type = "FeatureCollection",
                features
            },
            CableRoutePlanner.Verify.VerifyJson.Options));
    foreach (var route in routes)
    {
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{route.Name}: {route.Metrics.LengthMetres:F2} m, cost {route.Metrics.WeightedCost:F2}"));
    }

    return routes.Count == 0 ? 1 : 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
