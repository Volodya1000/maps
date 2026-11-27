"""Bounded adapter to existing independent constrained-mesh witness research."""

import argparse
import importlib.util
import json
import sqlite3
from contextlib import closing
from pathlib import Path

from routing_crossing_pilot import project, query
from routing_regression import audit_alternative, save_json
from shapely.geometry import Point, Polygon

ROOT = Path(__file__).resolve().parents[1]
PROBE_SOURCE = ROOT / "docs/evidence/routing-recovery/independent_probe.py"
RIVER_LINE_UNCERTAINTY_METRES = 1
POINT_PROOF_TOLERANCE_METRES = 1e-7
COVERAGE_PROOF_MARGIN_DEGREES = 1e-8
POINT_QUERY_MARGIN_DEGREES = 0.0001


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
