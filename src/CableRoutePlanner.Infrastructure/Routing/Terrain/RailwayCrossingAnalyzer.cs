using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.LinearReferencing;

namespace CableRoutePlanner.Infrastructure.Routing;

// Measures final route visits, independently of OSM IDs and routing costs.
internal sealed class RailwayCrossingAnalyzer
{
    private const double StationToleranceMetres = 0.000001;
    private const double ProbeDistanceMetres = 0.001;
    private const double AngleToleranceRadians = 0.0000001;
    private readonly STRtree<LineString> _tracks = new();
    private readonly STRtree<LineString> _sourceTracks = new();

    public RailwayCrossingAnalyzer(IEnumerable<Geometry> tracks)
    {
        // Node and dissolve coincident/partially overlapping ways, retaining each
        // separate parallel track. Junction rays are evaluated together below.
        Geometry[] input = tracks.ToArray();
        foreach (LineString source in input.SelectMany(Lines))
        {
            _sourceTracks.Insert(source.EnvelopeInternal, source);
        }
        _sourceTracks.Build();
        Geometry network = new GeometryFactory().BuildGeometry(input).Union();
        foreach (LineString line in Lines(network))
        {
            _tracks.Insert(line.EnvelopeInternal, line);
        }
        _tracks.Build();
    }

    public int Count(IReadOnlyList<Coordinate> route, CancellationToken cancellationToken)
    {
        if (route.Count < 2)
        {
            return 0;
        }
        LineString path = new(route.ToArray());
        LengthIndexedLine indexedRoute = new(path);
        List<double> stations = [];
        double offset = 0;
        for (int i = 1; i < route.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LineString segment = new([route[i - 1], route[i]]);
            foreach (LineString track in _tracks.Query(segment.EnvelopeInternal))
            {
                foreach (Coordinate contact in Points(segment.Intersection(track)))
                {
                    stations.Add(offset + route[i - 1].Distance(contact));
                }
            }
            offset += segment.Length;
        }
        int count = 0;
        double previous = double.NegativeInfinity;
        foreach (double station in stations.Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (station - previous <= StationToleranceMetres)
            {
                continue;
            }
            previous = station;
            if (station <= StationToleranceMetres || path.Length - station <= StationToleranceMetres)
            {
                continue;
            }
            Coordinate point = indexedRoute.ExtractPoint(station);
            double[] routeRays = RayAngles(path, indexedRoute, station, point).ToArray();
            double before = routeRays[0];
            double after = Normalize(routeRays[1] - before);
            if (after <= AngleToleranceRadians || Math.Tau - after <= AngleToleranceRadians)
            {
                continue; // route touches and returns along the same ray
            }
            count += CountAt(point, before, after);

        }
        return count;
    }

    private int CountAt(Coordinate point, double before, double after)
    {
        Envelope near = new(point);
        near.ExpandBy(StationToleranceMetres);
        List<double[]> continuations = [];
        List<double> tangentRays = [];
        List<double> endpoints = [];
        foreach (LineString track in _sourceTracks.Query(near))
        {
            LengthIndexedLine indexed = new(track);
            foreach (double station in StationsAt(track, point))
            {
                double[] rays = RayAngles(track, indexed, station, point)
                    .Select(angle => Normalize(angle - before)).ToArray();
                if (rays.Length == 1)
                {
                    if (!Aligned(rays[0], after))
                    {
                        endpoints.Add(rays[0]);
                    }
                    continue;
                }
                if (rays.Length != 2)
                {
                    continue;
                }
                // Preserve the original way's continuation through a node.
                // Two separate tangencies must not be paired into crossings.
                if (rays.Any(ray => Aligned(ray, after)) || (rays[0] < after) == (rays[1] < after))
                {
                    tangentRays.AddRange(rays);
                    continue;
                }
                continuations.Add(rays[0] < after ? rays : [rays[1], rays[0]]);
            }
        }
        List<double> unmatched = [];
        foreach (double ray in endpoints)
        {
            if (!tangentRays.Any(tangent => SameDirection(tangent, ray))
                && !unmatched.Any(existing => SameDirection(existing, ray)))
            {
                unmatched.Add(ray);
            }
        }
        // Split-way connections share the same ray capacities as complete ways.
        // A ray in a complete crossing can still be used by a different valid
        // matching when the input contains overlapping, partially split ways.
        foreach (double incoming in unmatched.Where(ray => ray < after))
        {
            foreach (double outgoing in unmatched.Where(ray => ray > after))
            {
                continuations.Add([incoming, outgoing]);
            }
        }
        return CountDistinctContinuations(continuations);
    }

    private static int CountDistinctContinuations(List<double[]> continuations)
    {
        List<double> incoming = [];
        List<double> outgoing = [];
        List<(int From, int To)> connections = [];
        foreach (double[] pair in continuations)
        {
            connections.Add((RayIndex(incoming, pair[0]), RayIndex(outgoing, pair[1])));
        }
        int[] matched = Enumerable.Repeat(-1, outgoing.Count).ToArray();
        int count = 0;
        for (int from = 0; from < incoming.Count; from++)
        {
            if (Assign(from, new bool[outgoing.Count]))
            {
                count++;
            }
        }
        return count;

        // Each physical ray has capacity one, even when several OSM ways share
        // it before a switch. Matching preserves the known continuations and
        // prevents either duplication or arbitrary source-order tie choices.
        bool Assign(int from, bool[] visited)
        {
            foreach ((int candidate, int to) in connections)
            {
                if (candidate != from || visited[to])
                {
                    continue;
                }
                visited[to] = true;
                if (matched[to] < 0 || Assign(matched[to], visited))
                {
                    matched[to] = from;
                    return true;
                }
            }
            return false;
        }
    }

    private static int RayIndex(List<double> rays, double angle)
    {
        int index = rays.FindIndex(existing => SameDirection(existing, angle));
        if (index >= 0)
        {
            return index;
        }
        rays.Add(angle);
        return rays.Count - 1;
    }

    private static bool SameDirection(double first, double second) =>
        Math.Abs(first - second) <= AngleToleranceRadians || Math.Tau - Math.Abs(first - second) <= AngleToleranceRadians;

    private static bool Aligned(double ray, double after) => SameDirection(ray, 0) || SameDirection(ray, after);

    private static IEnumerable<double> StationsAt(LineString track, Coordinate point)
    {
        double offset = 0;
        double previous = double.NegativeInfinity;
        double length = track.Length;
        Coordinate[] coordinates = track.Coordinates;
        for (int i = 1; i < coordinates.Length; i++)
        {
            LineSegment segment = new(coordinates[i - 1], coordinates[i]);
            Coordinate closest = segment.ClosestPoint(point);
            double station = offset + coordinates[i - 1].Distance(closest);
            if (closest.Distance(point) <= StationToleranceMetres && station - previous > StationToleranceMetres
                && !(track.IsClosed && length - station <= StationToleranceMetres))
            {
                yield return station;
                previous = station;
            }
            offset += segment.Length;
        }
    }

    private static IEnumerable<double> RayAngles(LineString line, LengthIndexedLine indexed,
        double station, Coordinate point)
    {
        double beforeDistance = station;
        double afterDistance = line.Length - station;
        double offset = 0;
        Coordinate[] coordinates = line.Coordinates;
        for (int i = 1; i < coordinates.Length; i++)
        {
            offset += coordinates[i - 1].Distance(coordinates[i]);
            double delta = offset - station;
            if (delta < -StationToleranceMetres)
            {
                beforeDistance = Math.Min(beforeDistance, -delta);
            }
            else if (delta > StationToleranceMetres)
            {
                afterDistance = Math.Min(afterDistance, delta);
                break;
            }
        }
        // Never probe across a neighboring vertex: even a short bend can turn
        // a touch into an apparent crossing if sampled on its far side.
        if (station > StationToleranceMetres)
        {
            yield return Angle(point, indexed.ExtractPoint(station - Math.Min(ProbeDistanceMetres, beforeDistance / 2)));
        }
        else if (line.IsClosed)
        {
            double lastSegmentLength = coordinates.Reverse().Skip(1).First(coordinate => coordinate.Distance(point) > StationToleranceMetres).Distance(point);
            yield return Angle(point, indexed.ExtractPoint(line.Length - Math.Min(ProbeDistanceMetres, lastSegmentLength / 2)));
        }
        if (line.Length - station > StationToleranceMetres)
        {
            yield return Angle(point, indexed.ExtractPoint(station + Math.Min(ProbeDistanceMetres, afterDistance / 2)));
        }
    }

    private static double Angle(Coordinate origin, Coordinate point) => Math.Atan2(point.Y - origin.Y, point.X - origin.X);
    private static double Normalize(double angle) => (angle % Math.Tau + Math.Tau) % Math.Tau;

    private static IEnumerable<LineString> Lines(Geometry geometry)
    {
        if (geometry is LineString line)
        {
            yield return line;
        }
        else if (geometry is GeometryCollection collection)
        {
            foreach (LineString part in collection.Geometries.SelectMany(Lines))
            {
                yield return part;
            }
        }
    }

    private static IEnumerable<Coordinate> Points(Geometry geometry)
    {
        if (geometry is Point point && !point.IsEmpty)
        {
            yield return point.Coordinate;
        }
        else if (geometry is GeometryCollection collection)
        {
            foreach (Coordinate coordinate in collection.Geometries.SelectMany(Points))
            {
                yield return coordinate;
            }
        }
    }
}
