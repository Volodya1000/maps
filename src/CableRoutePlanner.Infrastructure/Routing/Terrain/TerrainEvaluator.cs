using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Infrastructure.Geography;
using NetTopologySuite.Geometries;

namespace CableRoutePlanner.Infrastructure.Routing;

/// <summary>Checks a complete segment against prepared spatial data and returns measured engineering facts.</summary>
/// <remarks>
/// The search uses projected lengths; final metrics use geodesic lengths with bounded
/// terrain scaling. Geometry preparation and whole-route postprocessing have separate
/// owners. A null result always means the segment is inadmissible.
/// </remarks>
internal sealed class TerrainEvaluator
{
    private readonly GeometryFactory _factory = new();
    private readonly TerrainRules _rules;
    private readonly bool _preferGas;
    private readonly bool _allowUnderpasses;
    public PreparedTerrainData Terrain { get; }

    public TerrainEvaluator(PreparedTerrainData terrain, TerrainRules? rules = null, bool preferGas = false, bool allowUnderpasses = false)
    {
        Terrain = terrain;
        _rules = rules ?? new TerrainRules();
        _preferGas = preferGas;
        _allowUnderpasses = allowUnderpasses;
    }

    public TerrainEvaluator(IReadOnlyList<MapFeature> features, LocalProjection projection,
        TerrainRules? rules = null, bool preferGas = false, Geometry? coverage = null, Envelope? fullyCoveredArea = null, bool allowUnderpasses = false)
        : this(new PreparedTerrainData(features, projection, coverage, fullyCoveredArea), rules, preferGas, allowUnderpasses) { }

    /// <summary>Returns complete segment facts with geodesic length, or null when coverage or policy forbids it.</summary>
    public EdgeEvaluation? Evaluate(Coordinate a, Coordinate b)
        => Evaluate(a, b, useProjectedLength: false);

    /// <summary>Uses the same permissions with planar metre lengths consistent with the graph's heuristic.</summary>
    public EdgeEvaluation? EvaluateForSearch(Coordinate a, Coordinate b)
        => Evaluate(a, b, useProjectedLength: true);

    private EdgeEvaluation? Evaluate(Coordinate a, Coordinate b, bool useProjectedLength)
    {
        LineString line = _factory.CreateLineString([a, b]);
        if (Terrain.Coverage is not null && !(Terrain.FullyCoveredArea?.Covers(line.EnvelopeInternal) ?? false)
            && !Terrain.Coverage.Covers(line))
        {
            return null;
        }
        var contribution = new TerrainContribution();
        SortedSet<long>? gasCandidates = null;
        HashSet<long>? gasRoads = null;
        foreach (TerrainObstacle obstacle in Terrain.Index.Query(line.EnvelopeInternal))
        {
            Geometry geometry = obstacle.Geometry;
            if (!obstacle.PreparedGeometry.Intersects(line))
            {
                continue;
            }

            ITerrainRule rule = _rules.Get(obstacle.Kind);
            if (rule.Blocks)
            {
                return null;
            }

            // Explicitly registered passive objects need no overlay operation.
            // Gas candidates contribute only through their local road match below.
            if (TerrainTraversalPolicy.For(obstacle.Kind) == TerrainTraversalRule.Neutral
                || obstacle.Kind == TerrainKind.GasPipelineCrossingCandidate)
            {
                continue;
            }

            Geometry intersection = obstacle.PreparedGeometry.Covers(line)
                ? line
                : geometry.Intersection(line);
            if (obstacle.Kind == TerrainKind.Road)
            {
                foreach (Point crossing in TerrainRules.PointComponents(intersection))
                {
                    long[] bridgeCandidates = Terrain.BridgeCrossingCandidates
                        .Where(candidate => candidate.Geometry.Intersects(geometry)
                            && candidate.Corridor.Covers(crossing))
                        .Select(candidate => candidate.Id)
                        .ToArray();
                    IReadOnlyList<long> gasCandidateIds = Terrain.GasCrossings.UsedBy(line, geometry, intersection, crossing);
                    bool underpass = _allowUnderpasses && Terrain.UnderpassCrossings.UsedBy(line, geometry, intersection, crossing, obstacle.Id).Count > 0;
                    if (!TerrainTraversalPolicy.AllowsRoadCrossing(
                        bridgeCandidates.Length > 0, gasCandidateIds.Count > 0)
                        && !TerrainTraversalPolicy.AllowsPedestrianUnderpass(_allowUnderpasses, underpass))
                    {
                        return null;
                    }

                    contribution.BridgeCandidates.UnionWith(bridgeCandidates);
                    if (gasCandidateIds.Count > 0)
                    {
                        gasCandidates ??= [];
                        gasRoads ??= [];
                        gasCandidates.UnionWith(gasCandidateIds);
                        gasRoads.Add(obstacle.Id);
                    }
                }
            }

            rule.Apply(intersection, obstacle.Id, contribution);
        }

        double projectedLength = line.Length;
        double length = useProjectedLength
            ? projectedLength
            : Terrain.Projection.Inverse(a).DistanceTo(Terrain.Projection.Inverse(b));
        double scale = projectedLength > 0 ? length / projectedLength : 1;
        // Layers may overlap; each separate layer is bounded by segment length.
        // Scale planar metres to the chosen length metric, then clamp once more
        // because geodesic conversion can introduce a rounding excess.
        double ScaledTerrainLength(double measured) => Math.Min(Math.Min(measured, projectedLength) * scale, length);
        return new(
            length,
            ScaledTerrainLength(contribution.Forest),
            ScaledTerrainLength(contribution.Field),
            ScaledTerrainLength(contribution.Clearing),
            contribution.Roads.ToArray(),
            contribution.Crossings.ToArray(),
            contribution.BridgeCandidates.ToArray(),
            gasCandidates?.ToArray() ?? [],
            gasRoads?.Count ?? 0,
            _preferGas);
    }
}
