using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Projections;
using Mapsui.Providers;
using NetTopologySuite.Geometries;

namespace CableRoutePlanner.UI.MapAdapters;

internal sealed class GeodataFeatureProvider(IGeodataRepository geodata) : IProvider
{
    private int _readyAnnounced;

    public string? CRS { get; set; } = "EPSG:3857";

    public MRect GetExtent()
    {
        Coordinate bottomLeft = MapsuiMapAdapter.Project(
            new GeoCoordinate(geodata.Info.Bounds.South, geodata.Info.Bounds.West));
        Coordinate topRight = MapsuiMapAdapter.Project(
            new GeoCoordinate(geodata.Info.Bounds.North, geodata.Info.Bounds.East));
        return new MRect(bottomLeft.X, bottomLeft.Y, topRight.X, topRight.Y);
    }

    public async Task<IEnumerable<IFeature>> GetFeaturesAsync(FetchInfo fetchInfo)
    {
        MRect extent = fetchInfo.Extent;
        (double west, double south) = SphericalMercator.ToLonLat(extent.MinX, extent.MinY);
        (double east, double north) = SphericalMercator.ToLonLat(extent.MaxX, extent.MaxY);
        IReadOnlyList<MapFeature> features = await geodata.QueryMapAsync(
            new GeoBounds(west, south, east, north),
            fetchInfo.Resolution,
            CancellationToken.None).ConfigureAwait(false);
        IFeature[] prepared = MapsuiMapAdapter.PrepareFeatures(features, CancellationToken.None);
        if (prepared.Length > 0 && Interlocked.Exchange(ref _readyAnnounced, 1) == 0)
        {
            // Android routes stdout to logcat for the offline-map startup smoke test.
            Console.WriteLine(
                $"CRP_OFFLINE_MAP_READY region={geodata.Info.RegionId} features={prepared.Length} mode=raster-tiles");
        }

        return prepared;
    }
}
