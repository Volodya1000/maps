"""Bounded adapter to existing independent constrained-mesh witness research."""

import argparse
import copy
import hashlib
import importlib.util
import json
import math
import re
import sqlite3
from contextlib import closing
from pathlib import Path

from routing_crossing_pilot import project, query
from routing_regression import audit_alternative, save_json, search_meshes
from shapely.geometry import LineString, Point, Polygon, box, mapping
from shapely.ops import unary_union

ROOT = Path(__file__).resolve().parents[1]
PROBE_SOURCE = ROOT / "docs/evidence/routing-recovery/independent_probe.py"
RIVER_LINE_UNCERTAINTY_METRES = 1
POINT_PROOF_TOLERANCE_METRES = 1e-7
COVERAGE_PROOF_MARGIN_DEGREES = 1e-8
POINT_QUERY_MARGIN_DEGREES = 0.0001
WATER_RULE_SOURCE = (
    ROOT / "src/CableRoutePlanner.Infrastructure/Routing/Terrain/PreparedTerrainData.cs"
)
# NetTopologySuite 2.6.0 BufferParameters.DefaultQuadrantSegments. GEOS's
# default 16 would produce a larger corridor near round caps than the model.
MODEL_BUFFER_QUADRANT_SEGMENTS = 8


def _bridge_corridor_rule():
    try:
        source = WATER_RULE_SOURCE.read_bytes()
        text = source.decode("utf-8")
    except (OSError, UnicodeError):
        return None  # An unavailable model rule cannot support an independent proof.
    text = re.sub(r"/\*.*?\*/|//[^\n]*", "", text, flags=re.DOTALL)
    declarations = re.findall(
        r"^\s*public\s+const\s+double\s+BridgeCorridorToleranceMetres\s*=\s*"
        r"([0-9]+(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)[dD]?\s*;\s*$",
        text,
        flags=re.MULTILINE,
    )
    if len(declarations) != 1:
        return None
    value = float(declarations[0])
    if not math.isfinite(value) or value <= 0:
        return None
    return value, hashlib.sha256(source).hexdigest()


def _water_feature_identity(feature):
    return {
        "featureId": feature["id"],
        "sourceId": feature["source_id"],
        "kind": feature["kind"],
    }


def water_audit_scope_proofs(connection, record, alternative, checks):
    """Prove a water-rule mismatch or actual uncovered contact, never route validity.

    The immutable auditor activates a bridge per violated water feature. The
    current model activates it against any water in its full mesh snapshot,
    then subtracts that corridor globally. All input checks remain untouched.
    Missing model, mesh or geometry evidence produces no proof.
    """
    meshes = (
        search_meshes(record.get("diagnostics")) if isinstance(record, dict) else None
    )
    rule = _bridge_corridor_rule()
    if (
        not meshes
        or rule is None
        or not isinstance(checks, dict)
        or checks.get("status") != "violation"
        or not isinstance(checks.get("findings"), list)
    ):
        return []
    mesh = meshes[-1]
    bounds = [mesh[key] for key in ("West", "South", "East", "North")]
    origin = ((bounds[0] + bounds[2]) / 2, (bounds[1] + bounds[3]) / 2)
    threshold, source_sha256 = rule
    try:
        coordinates = alternative["geometry"]
        if not isinstance(coordinates, list) or len(coordinates) < 2:
            return []
        if any(not isinstance(point, dict) for point in coordinates):
            return []
        if any(
            type(point.get(key)) not in (int, float) or not math.isfinite(point[key])
            for point in coordinates
            for key in ("Longitude", "Latitude")
        ):
            return []
        route_geographic = LineString(
            [(point["Longitude"], point["Latitude"]) for point in coordinates]
        )
        if (
            not route_geographic.is_valid
            or route_geographic.length == 0
            or not box(*bounds).covers(route_geographic)
        ):
            return []
        route = project(route_geographic, origin)
        # This is the model's exact mesh bbox query, not a route envelope query.
        features = query(
            connection,
            bounds,
            kinds=("River", "Lake", "BridgeCandidate", "ApprovedCrossing"),
        )
        if any(
            feature["geometry"].is_empty or not feature["geometry"].is_valid
            for feature in features
        ):
            return []
        projected = [
            (feature, project(feature["geometry"], origin)) for feature in features
        ]
        waters = [
            (feature, geometry)
            for feature, geometry in projected
            if feature["kind"] in ("River", "Lake")
        ]
        approvals = [
            (feature, geometry)
            for feature, geometry in projected
            if feature["kind"] == "ApprovedCrossing"
            and isinstance(feature["approval_id"], str)
            and feature["approval_id"].strip()
        ]
        activated = []
        bridge_candidates = []
        for feature, geometry in projected:
            if feature["kind"] != "BridgeCandidate":
                continue
            activating = [
                water for water, shape in waters if shape.intersects(geometry)
            ]
            bridge_candidates.append(
                {
                    **_water_feature_identity(feature),
                    "activatingWaters": [
                        _water_feature_identity(water) for water in activating
                    ],
                }
            )
            if activating:
                activated.append(
                    (
                        feature,
                        geometry,
                        geometry.buffer(
                            threshold, quad_segs=MODEL_BUFFER_QUADRANT_SEGMENTS
                        ),
                        activating,
                    )
                )
        global_openings = unary_union(
            [geometry for _, geometry in approvals]
            + [corridor for _, _, corridor, _ in activated]
        )
        proofs = []
        for index, finding in enumerate(checks["findings"]):
            if (
                not isinstance(finding, dict)
                or finding.get("reason") != "water_without_opening"
            ):
                continue
            matches = [
                (feature, geometry)
                for feature, geometry in waters
                if type(finding.get("featureId")) is int
                and feature["id"] == finding["featureId"]
            ]
            if len(matches) != 1:
                continue
            water, geometry = matches[0]
            if finding.get("sourceId", water["source_id"]) != water["source_id"]:
                continue
            contact = route.intersection(geometry)
            if contact.is_empty:
                continue
            local_openings = unary_union(
                [shape for _, shape in approvals]
                + [
                    corridor
                    for _, bridge, corridor, _ in activated
                    if bridge.intersects(geometry)
                ]
            )
            # Match the model's residual obstacle, including its closed
            # boundary. Subtracting from the contact first loses that boundary.
            local_uncovered = route.intersection(geometry.difference(local_openings))
            uncovered = route.intersection(geometry.difference(global_openings))
            if uncovered.is_empty and local_uncovered.is_empty:
                continue
            bridges = [
                {
                    **_water_feature_identity(feature),
                    "activatingWaters": [
                        _water_feature_identity(activator) for activator in activating
                    ],
                }
                for feature, _, corridor, activating in activated
                if corridor.intersects(contact)
            ]
            approved = [
                _water_feature_identity(feature)
                for feature, shape in approvals
                if shape.intersects(contact)
            ]
            identities = [
                water,
                *[
                    feature
                    for feature, _, corridor, _ in activated
                    if corridor.intersects(contact)
                ],
                *[
                    activator
                    for _, _, corridor, activating in activated
                    if corridor.intersects(contact)
                    for activator in activating
                ],
            ]
            identities += [
                feature for feature, shape in approvals if shape.intersects(contact)
            ]
            if (uncovered.is_empty and not bridges) or any(
                not isinstance(feature["source_id"], str)
                or not feature["source_id"].strip()
                for feature in identities
            ):
                continue
            proofs.append(
                {
                    "kind": "auditor_model_rule_mismatch"
                    if uncovered.is_empty
                    else "independent_water_violation",
                    "findingIndex": index,
                    "originalFinding": copy.deepcopy(finding),
                    "water": _water_feature_identity(water),
                    "bridges": bridges,
                    "bridgeCandidates": copy.deepcopy(bridge_candidates),
                    "queriedWaterFeatures": [
                        _water_feature_identity(feature) for feature, _ in waters
                    ],
                    "approvedCrossings": approved,
                    "meshBounds": bounds,
                    "projectionOrigin": list(origin),
                    "bridgeCorridorToleranceMetres": threshold,
                    "bufferQuadrantSegments": MODEL_BUFFER_QUADRANT_SEGMENTS,
                    "thresholdSource": str(WATER_RULE_SOURCE),
                    "thresholdSourceSha256": source_sha256,
                    "contactLengthMetres": contact.length,
                    "localUncoveredLengthMetres": local_uncovered.length,
                    "uncoveredEmpty": uncovered.is_empty,
                    "uncoveredLengthMetres": uncovered.length,
                    "uncoveredGeometry": mapping(uncovered),
                    "uncoveredWkt": uncovered.wkt,
                    "scope": "water opening rule only; does not prove engineering approval or full route validity",
                }
            )
        return proofs
    except (KeyError, TypeError, ValueError, OverflowError):
        return []  # Incomplete or invalid evidence must never relax a finding.


def point_refusal_proof(connection, coverage, scenario, error):
    """Independent point evidence matching the unchanged placement policy."""
    reason = error.get("reason")
    terrain = error.get("terrainKind")
    for index, coordinate in enumerate(scenario["points"]):
        origin = (coordinate["Longitude"], coordinate["Latitude"])
        point = Point(origin)
        if (
            reason == "PointOutsidePackage"
            and not coverage.covers(point)
            and coverage.boundary.distance(point) > COVERAGE_PROOF_MARGIN_DEGREES
        ):
            return {
                "reason": reason,
                "kind": "outside_package_endpoint",
                "pointIndex": index,
                "terrainKind": None,
            }
        if reason != "BlockedTerrain" or terrain not in (
            "Building",
            "Marsh",
            "Lake",
            "River",
        ):
            continue
        margin_degrees = POINT_QUERY_MARGIN_DEGREES
        bounds = (
            origin[0] - margin_degrees,
            origin[1] - margin_degrees,
            origin[0] + margin_degrees,
            origin[1] + margin_degrees,
        )
        for feature in query(connection, bounds, kinds=(terrain,)):
            geometry = feature["geometry"]
            covered = geometry.covers(point)
            line_river = terrain == "River" and geometry.geom_type in (
                "LineString",
                "MultiLineString",
            )
            if covered or (
                line_river
                and project(geometry, origin).distance(Point(0, 0))
                < RIVER_LINE_UNCERTAINTY_METRES - POINT_PROOF_TOLERANCE_METRES
            ):
                return {
                    "reason": reason,
                    "kind": "blocked_endpoint",
                    "pointIndex": index,
                    "terrainKind": terrain,
                    "featureId": feature["id"],
                    "sourceId": feature["source_id"],
                }
    return None


def probe(data, scenario_path, record_path, output):
    specification = importlib.util.spec_from_file_location(
        "independent_research_probe", PROBE_SOURCE
    )
    module = importlib.util.module_from_spec(specification)
    specification.loader.exec_module(module)
    scenario = json.loads(scenario_path.read_text(encoding="utf-8"))
    record = json.loads(record_path.read_text(encoding="utf-8"))
    coverage = Polygon(json.loads((ROOT / "data/belarus-coverage.json").read_text()))
    with closing(
        sqlite3.connect(
            (data / "geodata.sqlite").resolve().as_uri() + "?mode=ro", uri=True
        )
    ) as connection:
        result = module.probe_case(
            connection,
            coverage,
            scenario,
            record,
            margin="mesh",
            width=0.2,
            max_features=3000,
            max_vertices=60000,
        )
        # Re-audit every accepted proposal; the search representation never grants permission.
        if result.get("status") == "witness":
            result["audit"] = audit_alternative(
                connection,
                coverage,
                scenario,
                result["alternative"],
                enabled=record["effectiveUnderpasses"],
            )
            if result["audit"]["status"] != "valid":
                result.update(status="unresolved", reason="candidate_failed_full_audit")
    save_json(output, result)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--scenario", type=Path, required=True)
    parser.add_argument("--record", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    probe(args.data, args.scenario, args.record, args.output)


if __name__ == "__main__":
    main()
