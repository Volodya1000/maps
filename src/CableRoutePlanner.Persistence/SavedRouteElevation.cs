using System.Text.Json;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;

namespace CableRoutePlanner.Persistence;

internal static class SavedRouteElevation
{
    public static SavedRoute Restore(SavedRoute route, JsonElement alternatives, JsonSerializerOptions options)
    {
        RouteAlternative[] restored = new RouteAlternative[route.Alternatives.Count];
        for (int index = 0; index < restored.Length; index++)
        {
            restored[index] = RestoreAlternative(route.Alternatives[index], alternatives[index], options);
        }
        return route with { Alternatives = restored };
    }

    private static RouteAlternative RestoreAlternative(RouteAlternative route, JsonElement json, JsonSerializerOptions options)
    {
        if (!json.TryGetProperty("ElevationProfile", out JsonElement profile))
        {
            return route;
        }

        JsonElement metrics = json.GetProperty("Metrics");
        double? savedSurfaceLength = metrics.GetProperty("SurfaceLengthMetres").Deserialize<double?>();
        if (profile.ValueKind != JsonValueKind.Null)
        {
            ElevationSource? source = profile.GetProperty("Source").Deserialize<ElevationSource>(options);
            RouteElevationSample[] samples = profile.GetProperty("Samples").Deserialize<RouteElevationSample[]>(options)!;
            double horizontalLength = profile.GetProperty("HorizontalLengthMetres").GetDouble();
            double? savedAscent = route.Metrics.AscentMetres;
            route = route.WithElevationSamples(source, samples, horizontalLength);
            RouteElevationProfile restored = route.ElevationProfile!;
            Check("ElevationProfile.SurfaceLengthMetres", profile.GetProperty("SurfaceLengthMetres").Deserialize<double?>(), restored.SurfaceLengthMetres);
            Check("ElevationProfile.AscentMetres", profile.GetProperty("AscentMetres").Deserialize<double?>(), restored.AscentMetres);
            Check("ElevationProfile.DescentMetres", profile.GetProperty("DescentMetres").Deserialize<double?>(), restored.DescentMetres);
            Check("ElevationProfile.HasCompleteCoverage", profile.GetProperty("HasCompleteCoverage").GetBoolean(), restored.HasCompleteCoverage);
            Check("ElevationProfile.IsCableLengthApplicable", profile.GetProperty("IsCableLengthApplicable").GetBoolean(), restored.IsCableLengthApplicable);
            Check("Metrics.AscentMetres", savedAscent, route.Metrics.AscentMetres);
        }
        Check("Metrics.SurfaceLengthMetres", savedSurfaceLength, route.Metrics.SurfaceLengthMetres);
        return route;
    }

    private static void Check<T>(string field, T saved, T expected)
    {
        if (!EqualityComparer<T>.Default.Equals(saved, expected))
        {
            throw new JsonException($"{field}={saved}: expected {expected} for the saved route and samples.");
        }
    }
}
