"""Preserve small geometry and bounded checks for selected mass failures."""

import argparse
import json
from collections import Counter
from pathlib import Path

from routing_crossing_pilot import audit, feature, geojson, project, save_json, svg_map
from routing_indirect_study import requested_search_bounds
from shapely.geometry import LineString, Point, Polygon, box, shape

ROOT = Path(__file__).resolve().parents[1]


def diagnose(run_dir, output, ids):
    scenarios = {
        item["id"]: item
        for item in json.loads((run_dir / "scenarios.json").read_text())
    }
    coverage = Polygon(json.loads((ROOT / "data/belarus-coverage.json").read_text()))
    output.mkdir(parents=True, exist_ok=True)
    summary = []
    for case_id in ids:
        scenario = scenarios[case_id]
        folder = run_dir / case_id
        result = json.loads((folder / "result.json").read_text())
        raw = json.loads((folder / "input.geojson").read_text())["features"]
        features = [
            {
                "id": item["properties"]["featureId"],
                "kind": item["properties"]["kind"],
                "geometry": shape(item["geometry"]),
                "source_id": item["properties"].get("sourceId"),
                "approval_id": item["properties"].get("approvalId"),
            }
            for item in raw
            if "featureId" in item["properties"]
        ]
        points = [
            (point["Longitude"], point["Latitude"]) for point in scenario["points"]
        ]
        direct = LineString(points)
        origin = points[0]
        direct_audit = audit(
            project(direct, origin),
            features,
            coverage=project(coverage, origin),
            origin=origin,
        )
        target = next(
            item
            for item in features
            if item["id"] == scenario["targetRoad"]["featureId"]
        )
        candidates = [
            item for item in features if item["id"] in scenario["candidateIds"]
        ]
        search = box(*requested_search_bounds(scenario["points"]))
        candidate_details = [
            {
                "featureId": item["id"],
                "kind": item["kind"],
                "sourceId": item["source_id"],
                "intersectsTargetRoad": item["geometry"].intersects(target["geometry"]),
                "insideRequestedBounds": search.covers(item["geometry"]),
                "distanceFromDirectMetres": round(
                    project(item["geometry"], origin).distance(project(direct, origin)),
                    1,
                ),
            }
            for item in candidates
        ]
        finding_counts = Counter(item["reason"] for item in direct_audit["findings"])
        road_contacts = [
            item
            for item in direct_audit["findings"]
            if item["reason"] == "road_contact"
        ]
        diagnostic = {
            "id": case_id,
            "routerStatus": result["status"],
            "typedReason": (result.get("error") or {}).get("reason"),
            "directAuditStatus": direct_audit["status"],
            "directFindingCounts": dict(finding_counts),
            "forbiddenDirectRoadContacts": sum(
                item["allowed"] is False for item in road_contacts
            ),
            "targetRoadDirectContacts": sum(
                item["roadId"] == target["id"] for item in road_contacts
            ),
            "candidateChecks": candidate_details,
            "requestedSearchBounds": list(search.bounds),
            "effectiveCellMetres": None,
            "portalConnectionStatus": "unknown",
            "independentValidPathStatus": "unknown",
        }
        summary.append(diagnostic)
        case_output = output / case_id
        case_output.mkdir(exist_ok=True)
        save_json(case_output / "diagnostic.json", diagnostic)
        selected = [
            item
            for item in features
            if item["id"] == target["id"]
            or item["id"] in scenario["candidateIds"]
            or (item["kind"] == "Road" and item["geometry"].intersects(direct))
        ]
        clip = box(*scenario["auditBounds"])
        focused = [
            feature(Point(point), {"role": role})
            for role, point in zip(("A", "B"), points, strict=True)
        ] + [feature(direct, {"kind": "Direct", "audit": direct_audit["status"]})]
        for item in selected:
            geometry = item["geometry"].intersection(clip)
            if not geometry.is_empty:
                focused.append(
                    feature(
                        geometry,
                        {
                            "kind": item["kind"],
                            "featureId": item["id"],
                            "sourceId": item["source_id"],
                            "target": item["id"] == target["id"],
                            "candidateStatus": "geometry_only_unverified"
                            if item["id"] in scenario["candidateIds"]
                            else None,
                        },
                    )
                )
        save_json(case_output / "geometry.geojson", geojson(focused))
        svg_map(
            focused,
            [],
            case_id,
            result["status"],
            case_output / "map.svg",
            scenario["auditBounds"],
        )
    save_json(output / "diagnostics.json", summary)
    print(json.dumps(summary, ensure_ascii=False, indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("ids", nargs="+")
    args = parser.parse_args()
    diagnose(args.run, args.output, args.ids)


if __name__ == "__main__":
    main()
