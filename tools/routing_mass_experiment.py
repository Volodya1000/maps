"""Freeze a diverse Belarus corpus using the existing crossing pilot runner."""

import argparse
import hashlib
import json
import random
import sqlite3
from collections import Counter
from pathlib import Path

from routing_crossing_pilot import (
    OPTIONS,
    audit,
    distance,
    feature,
    generate,
    geojson,
    pair_around,
    project,
    query,
    run,
    save_json,
    unproject,
)
from shapely.geometry import LineString, Point, Polygon
from shapely.strtree import STRtree

ROOT = Path(__file__).resolve().parents[1]
WINDOWS = {
    "naliboki": (26.35, 53.75, 26.75, 54.00),
    "pukhovichi": (27.35, 52.70, 27.75, 53.00),
    "smolevichi": (28.02, 53.85, 28.42, 54.10),
    "brest": (23.55, 52.00, 24.05, 52.35),
    "gomel": (30.65, 52.20, 31.15, 52.55),
    "vitebsk": (29.85, 55.05, 30.35, 55.35),
}
TARGETS = {"bridge": 5, "gas": 2, "multi": 3, "no_candidate": 3, "control": 2}


def pick_cases(connection, area, bounds, existing, rng, coverage, database_hash):
    roads = query(connection, bounds, ("Road",))
    candidates = query(
        connection, bounds, ("BridgeCandidate", "GasPipelineCrossingCandidate")
    )
    tree = STRtree([item["geometry"] for item in candidates])
    pools = {name: [] for name in TARGETS}
    road_order = list(roads)
    rng.shuffle(road_order)
    for road in road_order:
        lines = (
            list(road["geometry"].geoms)
            if hasattr(road["geometry"], "geoms")
            else [road["geometry"]]
        )
        for line in lines[:2]:
            if line.geom_type != "LineString" or len(line.coords) < 2:
                continue
            nearby_candidates = [
                candidates[int(index)]
                for index in tree.query(line, predicate="intersects")
                if candidates[int(index)]["source_id"] != road["source_id"]
            ]
            for kind in TARGETS:
                if kind == "gas":
                    matches = [
                        item
                        for item in nearby_candidates
                        if item["kind"] == "GasPipelineCrossingCandidate"
                    ]
                elif kind == "bridge":
                    matches = [
                        item
                        for item in nearby_candidates
                        if item["kind"] == "BridgeCandidate"
                    ]
                else:
                    matches = []
                if kind in ("gas", "bridge") and not matches:
                    continue
                if kind == "no_candidate" and nearby_candidates:
                    continue
                target = matches[0] if matches else None
                index = (
                    min(
                        range(len(line.coords) - 1),
                        key=lambda i: LineString(line.coords[i : i + 2]).distance(
                            target["geometry"]
                        ),
                    )
                    if target
                    else (len(line.coords) - 1) // 2
                )
                pools[kind].append((road, line, index, target))
    chosen = []
    rejected = Counter()
    used_roads = {item.get("targetRoad", {}).get("sourceId") for item in existing}
    for kind, quota in TARGETS.items():
        rng.shuffle(pools[kind])
        for road, line, index, candidate in pools[kind]:
            if sum(item["scenarioType"] == kind for item, _ in chosen) >= quota:
                break
            if road["source_id"] in used_roads:
                continue
            origin = line.centroid.coords[0]
            projected = project(line, origin)
            if projected.length < 80:
                rejected["short_road"] += 1
                continue
            for offset in (350, 500, 750, 1000):
                try:
                    endpoints = pair_around(
                        projected,
                        index,
                        offset,
                        100 if kind == "control" else 0,
                        kind == "control",
                    )
                except ValueError:
                    rejected["degenerate_road"] += 1
                    break
                points = [unproject(*p, origin) for p in endpoints]
                if not all(coverage.covers(Point(point)) for point in points):
                    rejected["outside_coverage"] += 1
                    continue
                pad = 0.025
                sample_bounds = (
                    min(p[0] for p in points) - pad,
                    min(p[1] for p in points) - pad,
                    max(p[0] for p in points) + pad,
                    max(p[1] for p in points) + pad,
                )
                features = query(connection, sample_bounds)
                if any(
                    item["geometry"].covers(Point(point))
                    for item in features
                    if item["kind"] in ("Building", "Marsh", "River", "Lake")
                    for point in points
                ):
                    rejected["blocked_endpoint"] += 1
                    continue
                terrain = [
                    next(
                        (
                            item["kind"]
                            for item in features
                            if item["kind"] in ("Forest", "Field")
                            and item["geometry"].covers(Point(point))
                        ),
                        "unknown",
                    )
                    for point in points
                ]
                if "unknown" in terrain:
                    rejected["unknown_terrain"] += 1
                    continue
                direct = LineString(points)
                road_hits = [
                    item["id"]
                    for item in features
                    if item["kind"] == "Road" and direct.intersects(item["geometry"])
                ]
                if kind == "multi" and len(set(road_hits)) < 2:
                    rejected["single_barrier"] += 1
                    continue
                if kind == "control":
                    direct_audit = audit(
                        project(direct, origin),
                        features,
                        coverage=project(coverage, origin),
                        origin=origin,
                    )
                    if direct_audit["status"] != "valid":
                        rejected["invalid_control"] += 1
                        continue
                case_id = f"mass-{area}-{kind}-{len(chosen) + 1:02}"
                case = {
                    "id": case_id,
                    "area": area,
                    "category": "F"
                    if kind == "control"
                    else "E"
                    if kind == "multi"
                    else "A"
                    if kind in ("bridge", "gas")
                    else "B",
                    "scenarioType": kind,
                    "quality": "verified" if kind == "control" else "exploratory",
                    "distanceClass": "local",
                    "terrain": terrain,
                    "points": [
                        {"Latitude": point[1], "Longitude": point[0]}
                        for point in points
                    ],
                    "distanceMetres": distance(*points),
                    "auditBounds": sample_bounds,
                    "targetRoad": {
                        "featureId": road["id"],
                        "sourceId": road["source_id"],
                    },
                    "candidateIds": [candidate["id"]] if candidate else [],
                    "directRoadIds": sorted(set(road_hits)),
                    "routingOptions": OPTIONS,
                    "seed": 20261113,
                    "databaseSha256": database_hash,
                }
                if kind == "control":
                    case["witness"] = {
                        "kind": "direct",
                        "coordinates": list(direct.coords),
                    }
                chosen.append((case, features))
                used_roads.add(road["source_id"])
                break
        rejected[f"unfilled_{kind}"] = quota - sum(
            case["scenarioType"] == kind for case, _ in chosen
        )
    return chosen, rejected


def freeze(data, output):
    pilot = generate(data, output)
    manifest = json.loads((data / "manifest.json").read_text())
    coverage = Polygon(json.loads((ROOT / "data/belarus-coverage.json").read_text()))
    connection = sqlite3.connect(f"file:{data / 'geodata.sqlite'}?mode=ro", uri=True)
    rng = random.Random(20261113)
    rejected = {}
    cases = list(pilot)
    for area, bounds in WINDOWS.items():
        selected, counts = pick_cases(
            connection, area, bounds, cases, rng, coverage, manifest["database_sha256"]
        )
        rejected[area] = counts
        for case, features in selected:
            cases.append(case)
            folder = output / case["id"]
            folder.mkdir(exist_ok=True)
            save_json(folder / "scenario.json", case)
            save_json(
                folder / "input.geojson",
                geojson(
                    [
                        feature(Point(p["Longitude"], p["Latitude"]), {"role": role})
                        for role, p in zip(("A", "B"), case["points"], strict=True)
                    ]
                    + [
                        feature(
                            item["geometry"],
                            {
                                "kind": item["kind"],
                                "featureId": item["id"],
                                "sourceId": item["source_id"],
                                "approvalId": item["approval_id"],
                                "target": item["id"] == case["targetRoad"]["featureId"],
                            },
                        )
                        for item in features
                        if item["kind"] not in ("Forest", "Field")
                    ]
                ),
            )
    connection.close()
    frozen = ROOT / "samples/routing-crossing-mass-v1.json"
    save_json(output / "scenarios.json", cases)
    if (
        frozen.exists()
        and frozen.read_bytes() != (output / "scenarios.json").read_bytes()
    ):
        raise ValueError("mass corpus differs from frozen manifest")
    save_json(
        output / "generation.json",
        {
            "seed": 20261113,
            "accepted": len(cases),
            "new": len(cases) - len(pilot),
            "rejected": rejected,
            "databaseSha256": manifest["database_sha256"],
            "corpusSha256": hashlib.sha256(
                (output / "scenarios.json").read_bytes()
            ).hexdigest(),
        },
    )
    print(f"Frozen {len(cases)} scenarios; copy manifest to {frozen} before baseline")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("freeze", "run"))
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--verify-dll", type=Path)
    args = parser.parse_args()
    if args.mode == "freeze":
        freeze(args.data, args.output)
    else:
        if (ROOT / "samples/routing-crossing-mass-v1.json").read_bytes() != (
            args.output / "scenarios.json"
        ).read_bytes():
            raise ValueError("baseline requires frozen mass corpus")
        run(args.data, args.verify_dll, args.output)


if __name__ == "__main__":
    main()
