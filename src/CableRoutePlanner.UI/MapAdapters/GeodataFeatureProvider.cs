using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Projections;
using Mapsui.Providers;
using Mapsui.Nts;
using Mapsui.Styles;
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
        IFeature[] prepared = MapsuiMapAdapter.PrepareFeatures(
            features,
            CancellationToken.None,
            fetchInfo.Resolution);
        if (prepared.Length > 0 && Interlocked.Exchange(ref _readyAnnounced, 1) == 0)
        {
            // Android routes stdout to logcat for the offline-map startup smoke test.
            Console.WriteLine(
                $"CRP_OFFLINE_MAP_READY region={geodata.Info.RegionId} features={prepared.Length} mode=raster-tiles");
        }

        // Cached parent tiles remain visible while adjacent detail tiles load.
        // An opaque tile background prevents their enlarged labels and old roads
        // bleeding through already loaded, otherwise transparent detail tiles.
        var background = new GeometryFeature(new GeometryFactory().CreatePolygon([
            new Coordinate(extent.MinX, extent.MinY),
            new Coordinate(extent.MaxX, extent.MinY),
            new Coordinate(extent.MaxX, extent.MaxY),
            new Coordinate(extent.MinX, extent.MaxY),
            new Coordinate(extent.MinX, extent.MinY)]));
        background.Styles.Add(new VectorStyle
        {
            Fill = new Brush(MapsuiStyleFactory.MapBackground),
            Outline = null
        });
        return prepared.Prepend(background);
    }
}
