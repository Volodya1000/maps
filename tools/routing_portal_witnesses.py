"""Independently audit reproducible portal witnesses without changing the package."""

import argparse
import json
import math
import sqlite3
from contextlib import closing
from pathlib import Path

from routing_crossing_pilot import audit, project
from routing_regression import audit_alternative, digest, save_json
from shapely import from_wkb
from shapely.geometry import LineString, Polygon

ROOT = Path(__file__).resolve().parents[1]
CASE_SOURCES = {
    "mass-naliboki-bridge-01": "way/174737352",
    "mass-smolevichi-bridge-03": "way/914629062",
    "mass-vitebsk-bridge-04": "way/29878981",
}


def synthetic_witness_audit(*, include_bridge=True):
    """The same known-valid corridor witness as PortalEndpointRegressionTests."""

    def point(x, y):
        return (
            27 + x / (111195.08 * math.cos(math.radians(54))),
            54 + y / 111195.08,
        )

    def rectangle(x1, y1, x2, y2):
        return Polygon([point(x1, y1), point(x2, y1), point(x2, y2), point(x1, y2)])

    features = [
        {"id": 1, "kind": "Building", "geometry": rectangle(-500, 0, 161, 139)},
        {"id": 2, "kind": "Building", "geometry": rectangle(165, 0, 1000, 139)},
        {"id": 3, "kind": "River", "geometry": rectangle(-500, 140, 1000, 180)},
    ]
    if include_bridge:
        features.append(
            {
                "id": 4,
                "kind": "BridgeCandidate",
                "geometry": LineString(
                    [point(163, 130), point(163, 150), point(190, 170), point(190, 190)]
                ),
            }
        )
    witness = LineString(
        [
            point(163, 100),
            point(163, 130),
            point(163, 150),
            point(190, 170),
            point(190, 190),
            point(280, 220),
        ]
    )
    origin = point(0, 0)
    return audit(
        project(witness, origin),
        features,
        project(rectangle(0, 0, 392, 667), origin),
        origin,
        allow_underpasses=False,
    )


def investigate(database, corpus, coverage_path):
    """Audit exact bridge polylines in both orientations using read-only SQLite."""
    scenarios = {
        scenario["id"]: scenario
        for scenario in json.loads(corpus.read_text(encoding="utf-8"))
    }
    coverage = Polygon(json.loads(coverage_path.read_text(encoding="utf-8")))
    records = []
    with closing(
        sqlite3.connect(database.resolve().as_uri() + "?mode=ro", uri=True)
    ) as connection:
        for case_id, source_id in CASE_SOURCES.items():
            scenario = scenarios[case_id]
            enabled = scenario["routingOptions"].get(
                "AllowExperimentalPedestrianUnderpasses", False
            )
            if type(enabled) is not bool:
                raise ValueError(f"Invalid underpass flag for {case_id}")
            candidates = connection.execute(
                "SELECT id,wkb FROM features WHERE source_id=? AND kind='BridgeCandidate' ORDER BY id",
                (source_id,),
            ).fetchall()
            if not candidates:
                raise ValueError(f"Missing target bridge {source_id} for {case_id}")
            attempts = []
            for candidate_id, wkb in candidates:
                bridge = from_wkb(wkb)
                if not isinstance(bridge, LineString):
                    raise ValueError(f"Target bridge {source_id} is not a LineString")
                for reverse in (False, True):
                    vertices = list(bridge.coords)
                    if reverse:
                        vertices.reverse()
                    start, end = scenario["points"]
                    alternative = {
                        "geometry": [
                            start,
                            *[
                                {"Longitude": longitude, "Latitude": latitude}
                                for longitude, latitude in vertices
                            ],
                            end,
                        ]
                    }
                    checked = audit_alternative(
                        connection, coverage, scenario, alternative, enabled=enabled
                    )
                    attempts.append(
                        {
                            "candidateId": candidate_id,
                            "reverseBridge": reverse,
                            "geometry": alternative["geometry"],
                            "audit": checked,
                        }
                    )
            valid = any(attempt["audit"]["status"] == "valid" for attempt in attempts)
            records.append(
                {
                    "id": case_id,
                    "candidateSourceId": source_id,
                    "effectiveUnderpasses": enabled,
                    "attempts": attempts,
                    "validWitnessFound": valid,
                    "detourExistence": "confirmed" if valid else "unknown",
                }
            )
    return {
        "schema": "maps.routing-portal-witnesses.v1",
        "databaseSha256": digest(database),
        "corpusSha256": digest(corpus),
        "scope": "Exact target bridge vertices with straight approaches in both orientations; invalid witnesses do not prove that no other detour exists.",
        "records": records,
        "syntheticWitnessAudit": synthetic_witness_audit(),
    }


def search_bounds(result):
    """Read the geographic bounds actually emitted for this head calculation."""
    diagnostics = result.get("diagnostics")
    if not isinstance(diagnostics, dict) or diagnostics.get("droppedEvents") != 0:
        return None
    events = diagnostics.get("events")
    if not isinstance(events, list):
        return None
    meshes = [
        event.get("fields")
        for event in events
        if isinstance(event, dict) and event.get("eventId") == 5
    ]
    if len(meshes) != 1 or not isinstance(meshes[0], dict):
        return None
    bounds = [meshes[0].get(key) for key in ("West", "South", "East", "North")]
    if any(
        type(value) not in (int, float) or not math.isfinite(value) for value in bounds
    ):
        return None
    west, south, east, north = bounds
    return bounds if -180 <= west < east <= 180 and -90 <= south < north <= 90 else None


def compare_run_report(witness_report, head_report):
    """Only matching evidence inside the actual head search area proves a miss."""
    comparison = {
        "gatePassed": False,
        "missedWitnessIds": [],
        "cases": [],
        "failures": [],
    }
    if not isinstance(head_report, dict):
        comparison["failures"].append({"reason": "invalid_run_report"})
        return comparison
    provenance = head_report.get("provenance", {})
    mismatches = [
        key
        for key in ("databaseSha256", "corpusSha256")
        if not isinstance(provenance, dict)
        or not witness_report.get(key)
        or provenance.get(key) != witness_report[key]
    ]
    if mismatches:
        comparison["failures"].append(
            {"reason": "provenance_mismatch", "fields": mismatches}
        )
        return comparison
    records = witness_report["records"]
    results = head_report.get("results", [])
    if not isinstance(results, list):
        results = []
    inspected = [
        result
        for result in results
        if isinstance(result, dict)
        and result.get("group") == "frozen"
        and result.get("id") in CASE_SOURCES
    ]
    if (
        len(records) != len(CASE_SOURCES)
        or {record["id"] for record in records} != set(CASE_SOURCES)
        or len(inspected) != len(CASE_SOURCES)
        or {result["id"] for result in inspected} != set(CASE_SOURCES)
    ):
        comparison["failures"].append({"reason": "inventory_mismatch"})
        return comparison
    head_by_id = {result["id"]: result for result in inspected}
    for record in records:
        case_id = record["id"]
        head = head_by_id[case_id]
        case = {"id": case_id, "headStatus": head.get("status")}
        if (
            type(head.get("effectiveUnderpasses")) is not bool
            or head["effectiveUnderpasses"] != record["effectiveUnderpasses"]
        ):
            case["reason"] = "underpass_flag_mismatch"
        elif head.get("status") not in ("success", "no_route"):
            case["reason"] = "head_result_unavailable"
        elif head["status"] == "success" and head.get("auditStatus") != "valid":
            case["reason"] = "head_route_not_valid"
        else:
            valid = [
                attempt
                for attempt in record["attempts"]
                if attempt["audit"]["status"] == "valid"
            ]
            bounds = search_bounds(head)
            case["headSearchBounds"] = bounds
            if not valid:
                case["reason"] = "no_valid_witness"
            elif bounds is None:
                case["reason"] = "mesh_bounds_unavailable"
            else:
                west, south, east, north = bounds
                within = [
                    all(
                        west <= point["Longitude"] <= east
                        and south <= point["Latitude"] <= north
                        for point in attempt["geometry"]
                    )
                    for attempt in valid
                ]
                case["validWitnessesWithinSearchBounds"] = within
                if not any(within):
                    case["reason"] = "witness_outside_search_bounds"
                elif head["status"] == "success":
                    case["reason"] = "valid_route_found"
                else:
                    case["reason"] = "missed_valid_path"
                    comparison["missedWitnessIds"].append(case_id)
        if case["reason"] not in (
            "no_valid_witness",
            "witness_outside_search_bounds",
            "valid_route_found",
        ):
            comparison["failures"].append({"id": case_id, "reason": case["reason"]})
        comparison["cases"].append(case)
    comparison["gatePassed"] = not comparison["failures"]
    return comparison


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--run-report", type=Path)
    parser.add_argument(
        "--corpus", type=Path, default=ROOT / "samples/routing-crossing-mass-v1.json"
    )
    parser.add_argument(
        "--coverage", type=Path, default=ROOT / "data/belarus-coverage.json"
    )
    args = parser.parse_args(argv)
    report = investigate(args.data / "geodata.sqlite", args.corpus, args.coverage)
    if args.run_report:
        try:
            head_report = json.loads(args.run_report.read_text(encoding="utf-8"))
            report["comparison"] = compare_run_report(report, head_report)
            report["comparison"]["runReportSha256"] = digest(args.run_report)
        except (OSError, ValueError) as error:
            report["comparison"] = {
                "gatePassed": False,
                "missedWitnessIds": [],
                "cases": [],
                "failures": [
                    {"reason": "invalid_run_report", "errorType": type(error).__name__}
                ],
            }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    save_json(args.output, report)
    for record in report["records"]:
        print(
            record["id"],
            [attempt["audit"]["status"] for attempt in record["attempts"]],
            record["detourExistence"],
        )
    print("synthetic witness:", report["syntheticWitnessAudit"]["status"])
    return 0 if report.get("comparison", {}).get("gatePassed", True) else 1


if __name__ == "__main__":
    raise SystemExit(main())
