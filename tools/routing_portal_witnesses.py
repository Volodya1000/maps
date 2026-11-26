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
                        connection, coverage, scenario, alternative, enabled=True
                    )
                    attempts.append(
                        {
                            "candidateId": candidate_id,
                            "reverseBridge": reverse,
                            "audit": checked,
                        }
                    )
            valid = any(attempt["audit"]["status"] == "valid" for attempt in attempts)
            records.append(
                {
                    "id": case_id,
                    "candidateSourceId": source_id,
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument(
        "--corpus", type=Path, default=ROOT / "samples/routing-crossing-mass-v1.json"
    )
    parser.add_argument(
        "--coverage", type=Path, default=ROOT / "data/belarus-coverage.json"
    )
    args = parser.parse_args()
    report = investigate(args.data / "geodata.sqlite", args.corpus, args.coverage)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    save_json(args.output, report)
    for record in report["records"]:
        print(
            record["id"],
            [attempt["audit"]["status"] for attempt in record["attempts"]],
            record["detourExistence"],
        )
    print("synthetic witness:", report["syntheticWitnessAudit"]["status"])


if __name__ == "__main__":
    main()
