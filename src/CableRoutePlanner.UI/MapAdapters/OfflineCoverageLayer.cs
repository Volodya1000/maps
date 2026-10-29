using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using NetTopologySuite.Geometries;

namespace CableRoutePlanner.UI.MapAdapters;

internal sealed class OfflineCoverageLayer : BaseLayer
{
    private readonly DataPackageInfo _info;
    private readonly IFeature[] _boundaryFeatures;
    private readonly LabelStyle _notice = new()
    {
        Text = "Нет данных офлайн-пакета",
        ForeColor = new Color(65, 75, 83),
        BackColor = new Brush(new Color(245, 247, 248)),
        CollisionDetection = false,
        HorizontalAlignment = LabelStyle.HorizontalAlignmentEnum.Center,
        VerticalAlignment = LabelStyle.VerticalAlignmentEnum.Center
    };

    public OfflineCoverageLayer(DataPackageInfo info) : base("Граница офлайн-пакета")
    {
        _info = info;
        Style = null;
        GeoCoverage coverage = info.Coverage ?? throw new ArgumentException("Package has no coverage polygon.", nameof(info));
        Coordinate[] border = coverage.Boundary.Select(MapsuiMapAdapter.Project).ToArray();
        // Cover the area outside the extract, leaving its polygon as a hole.
        const double mercatorLimit = 20037508.342789244;
        var factory = new GeometryFactory();
        Coordinate minimum = MapsuiMapAdapter.Project(new GeoCoordinate(info.Bounds.South, info.Bounds.West));
        Coordinate maximum = MapsuiMapAdapter.Project(new GeoCoordinate(info.Bounds.North, info.Bounds.East));
        Geometry covered = factory.CreatePolygon(border).Intersection(factory.ToGeometry(new Envelope(minimum, maximum)));
        Geometry world = factory.ToGeometry(new Envelope(-mercatorLimit, mercatorLimit, -mercatorLimit, mercatorLimit));
        var mask = new GeometryFeature(world.Difference(covered));
        mask.Styles.Add(new VectorStyle { Fill = new Brush(new Color(224, 228, 231)), Outline = null });
        var line = new GeometryFeature(covered.Boundary);
        line.Styles.Add(new VectorStyle { Line = new Pen(new Color(111, 123, 131), 2) });
        _boundaryFeatures = [mask, line];
        Extent = line.Extent;
    }

    public override IEnumerable<IFeature> GetFeatures(MRect extent, double resolution)
    {
        (double longitude, double latitude) = SphericalMercator.ToLonLat(extent.Centroid.X, extent.Centroid.Y);
        if (_info.Contains(new GeoCoordinate(latitude, longitude)))
        {
            return _boundaryFeatures;
        }
        var notice = new GeometryFeature(new Point(extent.Centroid.X, extent.Centroid.Y));
        notice.Styles.Add(_notice);
        return _boundaryFeatures.Append(notice);
    }
}
