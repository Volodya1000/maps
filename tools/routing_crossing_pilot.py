"""Small, reproducible road-centred experiment on the verified Belarus SQLite package."""

import argparse
import hashlib
import json
import math
import os
import random
import sqlite3
import subprocess
from collections import Counter
from pathlib import Path

from benchmark_routes import run_bounded_command
from shapely import from_wkb
from shapely.geometry import LineString, Point, Polygon, box, mapping, shape
from shapely.ops import transform

SEED = 20261106
WINDOWS = {
    "naliboki": (26.35, 53.75, 26.75, 54.00),
    "pukhovichi": (27.35, 52.70, 27.75, 53.00),
    "smolevichi": (28.02, 53.85, 28.42, 54.10),
}
OPTIONS = {
    "CellSizeMetres": 25,
    "MaxNodes": 100000,
    "ReserveFraction": 0.1,
    "PreferOverheadGasCrossings": True,
    "MaxRouteLengthMetres": 1000000,
}
KINDS = (
    "Road",
    "BridgeCandidate",
    "GasPipelineCrossingCandidate",
    "Building",
    "Marsh",
    "River",
    "Lake",
    "Forest",
    "Field",
    "ApprovedCrossing",
)


def project(geometry, origin):
    sx = 111195.0802335 * math.cos(math.radians(origin[1]))
    return transform(
        lambda x, y, z=None: ((x - origin[0]) * sx, (y - origin[1]) * 111195.0802335),
        geometry,
    )


def unproject(x, y, origin):
    return (
        origin[0] + x / (111195.0802335 * math.cos(math.radians(origin[1]))),
        origin[1] + y / 111195.0802335,
    )


def distance(a, b):
    lat1, lat2 = map(math.radians, (a[1], b[1]))
    dlat, dlon = lat2 - lat1, math.radians(b[0] - a[0])
    h = (
        math.sin(dlat / 2) ** 2
        + math.cos(lat1) * math.cos(lat2) * math.sin(dlon / 2) ** 2
    )
    return 6371008.8 * 2 * math.asin(math.sqrt(min(1, h)))


def pair_around(road, segment_index, offset_metres, tangent_metres, same_side=False):
    a, b = list(road.coords)[segment_index : segment_index + 2]
    length = math.dist(a, b)
    if length == 0:
        raise ValueError("zero length road segment")
    tx, ty = (b[0] - a[0]) / length, (b[1] - a[1]) / length
    nx, ny = -ty, tx
    qx, qy = (a[0] + b[0]) / 2, (a[1] + b[1]) / 2
    return (
        (
            qx - tx * tangent_metres - nx * offset_metres,
            qy - ty * tangent_metres - ny * offset_metres,
        ),
        (
            qx + tx * tangent_metres + nx * offset_metres * (-1 if same_side else 1),
            qy + ty * tangent_metres + ny * offset_metres * (-1 if same_side else 1),
        ),
    )


def query(connection, bounds, kinds=KINDS):
    west, south, east, north = bounds
    placeholders = ",".join("?" for _ in kinds)
    sql = f"""SELECT f.id,f.kind,f.wkb,f.source_id,f.approval_id FROM features f
        JOIN feature_index i ON i.id=f.id WHERE i.minx<=? AND i.maxx>=?
        AND i.miny<=? AND i.maxy>=? AND f.kind IN ({placeholders}) ORDER BY f.id"""
    rows = connection.execute(sql, (east, west, north, south, *kinds))
    return [
        dict(
            id=row[0],
            kind=row[1],
            geometry=from_wkb(row[2]),
            source_id=row[3],
            approval_id=row[4],
        )
        for row in rows
    ]


def components(geometry):
    if geometry.geom_type == "Point":
        return [geometry]
    if hasattr(geometry, "geoms"):
        return [point for part in geometry.geoms for point in components(part)]
    return []


def audit(route, features, coverage=None, origin=None):
    """Independent overlay on stored WKB. Unknown is used where model evidence is incomplete."""
    findings = []
    if coverage is not None and not coverage.covers(route):
        findings.append({"reason": "outside_coverage"})
    projected = [
        (
            feature,
            project(feature["geometry"], origin) if origin else feature["geometry"],
        )
        for feature in features
    ]
    for feature, geometry in projected:
        if feature["kind"] in ("Building", "Marsh") and route.intersects(geometry):
            findings.append({"reason": "blocked_terrain", "featureId": feature["id"]})
        if feature["kind"] in ("River", "Lake") and route.intersects(geometry):
            approvals = [
                g
                for f, g in projected
                if f["kind"] == "ApprovedCrossing"
                and (f.get("approval_id") or "").strip()
            ]
            bridges = [
                g.buffer(2)
                for f, g in projected
                if f["kind"] == "BridgeCandidate" and g.intersects(geometry)
            ]
            opening = route.intersection(geometry)
            if not any(corridor.covers(opening) for corridor in approvals + bridges):
                findings.append(
                    {"reason": "water_without_opening", "featureId": feature["id"]}
                )
        if feature["kind"] != "Road":
            continue
        contact = route.intersection(geometry)
        points = components(contact)
        if not contact.is_empty and (not points or contact.length > 0):
            findings.append({"reason": "shared_road_section", "roadId": feature["id"]})
        for point in points:
            bridges = [
                f["id"]
                for f, g in projected
                if f["kind"] == "BridgeCandidate"
                and g.intersects(geometry)
                and g.buffer(2).covers(point)
            ]
            gas = []
            for f, g in projected:
                if f["kind"] != "GasPipelineCrossingCandidate" or not g.crosses(
                    geometry
                ):
                    continue
                if not g.buffer(1).covers(point):
                    continue
                section = route.intersection(geometry.buffer(3))
                if g.buffer(1).covers(section):
                    gas.append(f["id"])
            findings.append(
                {
                    "reason": "road_contact",
                    "roadId": feature["id"],
                    "coordinate": list(point.coords[0]),
                    "bridgeIds": bridges,
                    "gasIds": gas,
                    "allowed": bool(bridges or gas),
                }
            )
    bad = any(
        f.get("allowed") is False
        or f["reason"]
        in ("outside_coverage", "blocked_terrain", "water_without_opening")
        for f in findings
    )
    incomplete = any(f["reason"] in ("shared_road_section",) for f in findings)
    return {
        "status": "violation" if bad else "unknown" if incomplete else "valid",
        "findings": findings,
    }


def note_incomplete_audit(checks, reason):
    if checks["status"] != "violation":
        checks["status"] = "unknown"
    checks["findings"].append({"reason": reason})


def harness_integrity(scenarios, results):
    """Validate the completeness and internal consistency of observable records."""
    ids = [case.get("id") for case in scenarios]
    result_ids = [result.get("id") for result in results]
    if not ids or any(
        not isinstance(value, str) or not value for value in ids + result_ids
    ):
        return False
    if (
        len(ids) != len(set(ids))
        or len(result_ids) != len(set(result_ids))
        or set(ids) != set(result_ids)
    ):
        return False
    for result in results:
        status = result.get("status")
        alternatives = result.get("alternatives")
        audit_result = result.get("audit")
        if (
            status
            not in (
                "success",
                "no_route",
                "timeout",
                "resource_limit",
                "error",
                "invalid_input",
            )
            or not isinstance(alternatives, list)
            or not isinstance(audit_result, dict)
        ):
            return False
        audits = audit_result.get("alternatives")
        if not isinstance(audits, list) or len(alternatives) != len(audits):
            return False
        if status != "success":
            if alternatives or audit_result.get("status") != "unknown":
                return False
            continue
        if not alternatives or audit_result.get("status") != "valid":
            return False
        for alternative, check in zip(alternatives, audits, strict=True):
            if not isinstance(alternative, dict) or not isinstance(check, dict):
                return False
            geometry = alternative.get("geometry")
            if (
                not alternative.get("name")
                or not isinstance(geometry, list)
                or len(geometry) < 2
            ):
                return False
            if not all(
                isinstance(point, dict)
                and isinstance(point.get("Latitude"), (int, float))
                and isinstance(point.get("Longitude"), (int, float))
                and math.isfinite(point["Latitude"])
                and math.isfinite(point["Longitude"])
                for point in geometry
            ):
                return False
            if check.get("status") != "valid" or not isinstance(
                check.get("findings"), list
            ):
                return False
            for finding in check["findings"]:
                if (
                    not isinstance(finding, dict)
                    or finding.get("reason") != "road_contact"
                ):
                    return False
                if (
                    finding.get("allowed") is not True
                    or not isinstance(finding.get("roadId"), int)
                    or not isinstance(finding.get("bridgeIds"), list)
                    or not isinstance(finding.get("gasIds"), list)
                    or not (finding["bridgeIds"] or finding["gasIds"])
                ):
                    return False
                coordinate = finding.get("coordinate")
                if (
                    not isinstance(coordinate, list)
                    or len(coordinate) != 2
                    or not all(
                        isinstance(x, (int, float)) and math.isfinite(x)
                        for x in coordinate
                    )
                ):
                    return False
    return True


def pilot_acceptance(scenarios, results, *, evidence=None):
    categories = Counter(s["category"] for s in scenarios)
    quality = Counter(s["quality"] for s in scenarios)
    by_id = {s["id"]: s for s in scenarios}
    routes_with_contacts = 0
    confirmed_missed = []
    failure_profiles = {
        name: Counter() for name in ("Короче", "Баланс", "Проще прокладка")
    }
    for result in results:
        case = by_id.get(result.get("id"), {})
        if case.get("witness") and result.get("status") == "no_route":
            confirmed_missed.append(case["id"])
        for alt in result.get("audit", {}).get("alternatives", []):
            if alt["status"] == "valid" and any(
                f.get("reason") == "road_contact" and f.get("allowed")
                for f in alt["findings"]
            ):
                routes_with_contacts += 1
        for item in (result.get("error") or {}).get("searchFailures", []):
            if item["profile"] in failure_profiles:
                failure_profiles[item["profile"]][item["reason"]] += 1
    positive = sum(
        bool(s.get("witness"))
        and s["quality"] == "verified"
        and s["category"] in ("A", "C")
        for s in scenarios
    )
    missing = [c for c in "ABCDEF" if not categories[c]]
    missing_requirements = []
    if not (
        8 <= len(scenarios) <= 12
        and categories["B"]
        and categories["E"]
        and categories["F"]
    ):
        missing_requirements.append("minimum scenario strata B/E/F or 8-12 cases")
    areas = {s.get("area") for s in scenarios} - {None}
    distances = Counter(s.get("distanceClass") for s in scenarios)
    if len(areas) < 2 or distances["local"] < 2 or distances["full"] < 2:
        missing_requirements.append("at least two AOIs, two local and two full cases")
    if not any(s["category"] == "F" and s["quality"] == "verified" for s in scenarios):
        missing_requirements.append("independent verified F control")
    if positive < 2:
        missing_requirements.append("two independent positive A/C witnesses")
    complete = (
        len(results) == len(scenarios)
        and len({r.get("id") for r in results}) == len(scenarios)
        and {r.get("id") for r in results} == set(by_id)
    )
    if not routes_with_contacts and complete:
        missing_requirements.append("successful model-eligible road crossing")
    if not complete:
        missing_requirements.append("all scenarios executed")
    if confirmed_missed:
        missing_requirements.append("verified-positive route missed by search")
    # These requirements are not established by a single automated cold run.
    unverified_requirements = [
        "manual review of all cases",
        "full per-candidate rejection log",
        "canonical manifest and per-scenario hashes",
        "repeat with semantic comparison",
        "checkpoint/resume verified",
        "warm local/full runs",
    ]
    if not routes_with_contacts and not complete:
        unverified_requirements.append("successful model-eligible road crossing")
    if any(r.get("audit", {}).get("status") == "violation" for r in results):
        unverified_requirements.append("audit violation investigated")
    # These external attestations describe evidence outside this one run. The
    # caller is responsible for validating the source, not the harness.
    evidence_assessments = {}
    for requirement, assessment in (evidence or {}).items():
        if (
            requirement not in unverified_requirements
            or assessment.get("status") not in ("met", "unmet", "unverified")
            or not assessment.get("source")
        ):
            raise ValueError("invalid external evidence assessment")
        evidence_assessments[requirement] = assessment
        if assessment["status"] != "unverified":
            unverified_requirements.remove(requirement)
            if assessment["status"] == "unmet":
                missing_requirements.append(requirement)
    integrity = harness_integrity(scenarios, results)
    if not integrity:
        missing_requirements.append(
            "complete and internally consistent harness results"
        )
    return {
        "areaCount": len(areas),
        "distanceClassCounts": {d: distances[d] for d in ("local", "full")},
        "categoryCounts": {c: categories[c] for c in "ABCDEF"},
        "qualityCounts": {q: quality[q] for q in ("verified", "exploratory")},
        "missingCategories": missing,
        "verifiedPositiveWitnesses": positive,
        "successfulRoutesWithAllowedRoadContact": routes_with_contacts,
        "confirmedMissedPaths": confirmed_missed,
        "failureReasonsByProfile": {k: dict(v) for k, v in failure_profiles.items()},
        "evidenceAssessments": evidence_assessments,
        "unmetRequirements": missing_requirements,
        "unverifiedRequirements": unverified_requirements,
        "unmetOrUnverifiedRequirements": missing_requirements + unverified_requirements,
        "harnessIntegrity": "pass" if integrity else "fail_or_incomplete",
        "pilotAccepted": False
        if missing_requirements
        else None
        if unverified_requirements
        else True,
    }


def geojson(features):
    return {"type": "FeatureCollection", "features": features}


def feature(geometry, properties):
    return {"type": "Feature", "geometry": mapping(geometry), "properties": properties}


def save_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n")


def generate(data, output, count=10, corpus_version="v2"):
    manifest = json.loads((data / "manifest.json").read_text())
    with (data / "geodata.sqlite").open("rb") as stream:
        if (
            hashlib.file_digest(stream, "sha256").hexdigest()
            != manifest["database_sha256"]
        ):
            raise ValueError("database SHA-256 mismatch")
    coverage = Polygon(json.loads(Path("data/belarus-coverage.json").read_text()))
    connection = sqlite3.connect(f"file:{data / 'geodata.sqlite'}?mode=ro", uri=True)
    rng = random.Random(SEED)
    rejected = Counter()
    scenarios = []
    inventory = {}
    quotas = {"naliboki": 4, "pukhovichi": 3, "smolevichi": 3}
    for name, bounds in WINDOWS.items():
        roads = query(connection, bounds, ("Road",))
        rng.shuffle(roads)
        gas = query(connection, bounds, ("GasPipelineCrossingCandidate",))
        preferred = []
        for candidate in gas:
            parts = candidate["source_id"].split(":")
            if len(parts) >= 3:
                preferred.append(parts[2])
        roads.sort(key=lambda road: 0 if road["source_id"] in preferred else 1)
        picked = 0
        for road in roads[:300]:
            if picked >= quotas[name]:
                break
            geometry = road["geometry"]
            lines = list(geometry.geoms) if hasattr(geometry, "geoms") else [geometry]
            for line in lines[:2]:
                if line.geom_type != "LineString" or len(line.coords) < 2:
                    continue
                matching_gas = [
                    candidate
                    for candidate in gas
                    if candidate["source_id"].split(":")[2:3] == [road["source_id"]]
                ]
                idx = (
                    min(
                        range(len(line.coords) - 1),
                        key=lambda i: LineString(line.coords[i : i + 2]).distance(
                            matching_gas[0]["geometry"]
                        ),
                    )
                    if matching_gas
                    else (len(line.coords) - 1) // 2
                )
                origin = line.centroid.coords[0]
                projected = project(line, origin)
                if projected.length < 100:
                    rejected["short_road"] += 1
                    continue
                is_control = picked == quotas[name] - 1 and name != "pukhovichi"
                is_full = picked == 1 or (name == "pukhovichi" and picked == 2)
                offsets = (5400, 6200, 6900, 7400) if is_full else (400, 600, 900, 1100)
                for offset in offsets:
                    try:
                        metres = pair_around(
                            projected, idx, offset, 250 if is_control else 0, is_control
                        )
                    except ValueError:
                        rejected["degenerate_segment"] += 1
                        continue
                    points = [unproject(*p, origin) for p in metres]
                    direct = LineString(metres)
                    length = distance(*points)
                    if not (
                        (10000 <= length <= 15000)
                        if is_full
                        else (500 <= length <= 3000)
                    ):
                        rejected["distance"] += 1
                        continue
                    if not all(coverage.covers(Point(p)) for p in points):
                        rejected["coverage"] += 1
                        continue
                    if not is_control and not direct.crosses(projected):
                        rejected["no_target_crossing"] += 1
                        continue
                    if is_control and direct.intersects(projected):
                        rejected["invalid_control"] += 1
                        continue
                    pad = 0.03
                    sample_bounds = (
                        min(p[0] for p in points) - pad,
                        min(p[1] for p in points) - pad,
                        max(p[0] for p in points) + pad,
                        max(p[1] for p in points) + pad,
                    )
                    nearby = query(connection, sample_bounds)
                    point_shapes = [Point(p) for p in points]
                    if any(
                        f["geometry"].covers(point)
                        for f in nearby
                        if f["kind"] in ("Building", "Marsh", "River", "Lake")
                        for point in point_shapes
                    ):
                        rejected["blocked_point"] += 1
                        continue
                    kinds = [
                        next(
                            (
                                f["kind"]
                                for f in nearby
                                if f["kind"] in ("Forest", "Field")
                                and f["geometry"].covers(point)
                            ),
                            "unknown",
                        )
                        for point in point_shapes
                    ]
                    if "unknown" in kinds:
                        rejected["unknown_terrain"] += 1
                        continue
                    road_mid = Point(origin)
                    if any(
                        f["kind"] == "Building"
                        and f["geometry"].distance(road_mid) < 0.001
                        for f in nearby
                    ):
                        rejected["building_near_road"] += 1
                        continue
                    if (
                        is_control
                        and audit(
                            direct,
                            nearby,
                            coverage=project(coverage, origin),
                            origin=origin,
                        )["status"]
                        != "valid"
                    ):
                        rejected["control_without_direct_witness"] += 1
                        continue
                    candidates = [
                        f
                        for f in nearby
                        if f["kind"]
                        in ("BridgeCandidate", "GasPipelineCrossingCandidate")
                    ]
                    target_candidates = [
                        f
                        for f in candidates
                        if f["geometry"].intersects(road["geometry"])
                    ]
                    category = "F" if is_control else "A" if target_candidates else "B"
                    scenario_id = f"{name}-{picked + 1:02}"
                    scenario = {
                        "id": scenario_id,
                        "area": name,
                        "category": category,
                        "quality": "verified" if is_control else "exploratory",
                        "distanceClass": "full" if is_full else "local",
                        "terrain": kinds,
                        "points": [
                            {"Latitude": p[1], "Longitude": p[0]} for p in points
                        ],
                        "distanceMetres": length,
                        "auditBounds": sample_bounds,
                        "targetRoad": {
                            "featureId": road["id"],
                            "sourceId": road["source_id"],
                        },
                        "candidateIds": [f["id"] for f in target_candidates],
                        "routingOptions": OPTIONS,
                        "seed": SEED,
                        "databaseSha256": manifest["database_sha256"],
                    }
                    scenarios.append(scenario)
                    inventory[scenario_id] = (nearby, road)
                    picked += 1
                    break
                if picked >= quotas[name]:
                    break
        if picked < quotas[name]:
            rejected[f"quota_unfilled_{name}"] = quotas[name] - picked
    original = json.dumps(scenarios, ensure_ascii=False, indent=2) + "\n"
    if original != Path("samples/routing-crossing-pilot.json").read_text():
        raise ValueError("generated v1 differs from frozen pilot corpus")
    witness = None
    if corpus_version == "v2":
        # Original E slot was selected before baseline as B. Reclassify after
        # checking separate intersections; keep its original failed result.
        complex_case = next(s for s in scenarios if s["id"] == "smolevichi-01")
        complex_case["category"] = "E"
        complex_case["classificationEvidence"] = {
            "directRoadIds": [1055591, 2926352, 2926353, 2930680],
            "classification": "retrospective; original selection was B before baseline",
        }
        original_features, _ = inventory["smolevichi-01"]
        direct = LineString(
            [(p["Longitude"], p["Latitude"]) for p in complex_case["points"]]
        )
        road_hits = {
            f["id"]
            for f in original_features
            if f["kind"] == "Road" and direct.intersects(f["geometry"])
        }
        if not set(complex_case["classificationEvidence"]["directRoadIds"]).issubset(
            road_hits
        ):
            raise ValueError("complex road evidence changed")
        coords = [
            (28.104615898132728, 53.993017548474526),
            (28.108090501867274, 53.98802325152547),
        ]
        witness = LineString(coords)
        pad = 0.03
        bounds2 = (
            min(x for x, _ in coords) - pad,
            min(y for _, y in coords) - pad,
            max(x for x, _ in coords) + pad,
            max(y for _, y in coords) + pad,
        )
        nearby = query(connection, bounds2)
        road = next(f for f in nearby if f["id"] == 2683950 and f["kind"] == "Road")
        candidate = next(
            f for f in nearby if f["id"] == 192543 and f["kind"] == "BridgeCandidate"
        )
        if not candidate["geometry"].intersects(road["geometry"]):
            raise ValueError("bridge is not eligible for target road")
        if any(
            f["kind"] in ("Building", "Marsh", "River", "Lake")
            and witness.intersects(f["geometry"])
            for f in nearby
        ):
            raise ValueError("independent witness touches blocked terrain")
        terrain = [
            next(
                (
                    f["kind"]
                    for f in nearby
                    if f["kind"] in ("Field", "Forest")
                    and f["geometry"].covers(Point(pt))
                ),
                "unknown",
            )
            for pt in coords
        ]
        if "unknown" in terrain:
            raise ValueError("witness endpoint terrain unknown")
        checks = audit(
            project(witness, coords[0]),
            nearby,
            coverage=project(coverage, coords[0]),
            origin=coords[0],
        )
        contacts = [f for f in checks["findings"] if f["reason"] == "road_contact"]
        if (
            checks["status"] != "valid"
            or not contacts
            or any(192543 not in c["bridgeIds"] for c in contacts)
        ):
            raise ValueError("witness bridge contacts not independently verified")
        case = {
            "id": "smolevichi-04",
            "area": "smolevichi",
            "category": "A",
            "quality": "verified",
            "distanceClass": "local",
            "terrain": terrain,
            "points": [{"Longitude": x, "Latitude": y} for x, y in coords],
            "distanceMetres": distance(*coords),
            "auditBounds": bounds2,
            "targetRoad": {"featureId": road["id"], "sourceId": road["source_id"]},
            "candidateIds": [candidate["id"]],
            "routingOptions": OPTIONS,
            "seed": SEED,
            "databaseSha256": manifest["database_sha256"],
            "witness": {
                "kind": "direct_bridge",
                "candidateId": candidate["id"],
                "roadIds": sorted(c["roadId"] for c in contacts),
            },
        }
        scenarios = [s for s in scenarios if s["id"] != "naliboki-01"] + [case]
        inventory[case["id"]] = (nearby, road)
    connection.close()
    output.mkdir(parents=True, exist_ok=True)
    save_json(output / "scenarios.json", scenarios)
    save_json(
        output / "generation.json",
        {
            "seed": SEED,
            "rejected": rejected,
            "accepted": len(scenarios),
            "databaseSha256": manifest["database_sha256"],
            "corpusVersion": corpus_version,
            "revisionNotes": "v2: retrospectively classified smolevichi-01 as E; replaced successful naliboki-01 with independently witnessed bridge case"
            if corpus_version == "v2"
            else "v1",
        },
    )
    frozen = Path(
        "samples/routing-crossing-pilot-v2.json"
        if corpus_version == "v2"
        else "samples/routing-crossing-pilot.json"
    )
    if (
        not frozen.exists()
        or frozen.read_bytes() != (output / "scenarios.json").read_bytes()
    ):
        raise ValueError("generated scenarios differ from frozen pilot corpus")
    for scenario in scenarios:
        sid = scenario["id"]
        nearby, road = inventory[sid]
        folder = output / sid
        folder.mkdir(exist_ok=True)
        save_json(folder / "scenario.json", scenario)
        if scenario["category"] == "F" or scenario.get("witness"):
            if scenario.get("witness"):
                checks = audit(
                    project(
                        witness,
                        (
                            scenario["points"][0]["Longitude"],
                            scenario["points"][0]["Latitude"],
                        ),
                    ),
                    nearby,
                    coverage=project(
                        coverage,
                        (
                            scenario["points"][0]["Longitude"],
                            scenario["points"][0]["Latitude"],
                        ),
                    ),
                    origin=(
                        scenario["points"][0]["Longitude"],
                        scenario["points"][0]["Latitude"],
                    ),
                )
                save_json(folder / "witness-audit.json", checks)
            save_json(
                folder / "witness.geojson",
                geojson(
                    [
                        feature(
                            LineString(
                                [
                                    (point["Longitude"], point["Latitude"])
                                    for point in scenario["points"]
                                ]
                            ),
                            {"kind": "verified_direct_witness"},
                        )
                    ]
                ),
            )
        save_json(
            folder / "input.geojson",
            geojson(
                [
                    feature(Point(p["Longitude"], p["Latitude"]), {"role": label})
                    for label, p in zip(("A", "B"), scenario["points"], strict=True)
                ]
                + [
                    feature(
                        f["geometry"],
                        {
                            "kind": f["kind"],
                            "featureId": f["id"],
                            "sourceId": f["source_id"],
                            "approvalId": f["approval_id"],
                            "target": f["id"] == road["id"],
                        },
                    )
                    for f in nearby
                    if f["kind"] not in ("Forest", "Field")
                ]
            ),
        )
    return scenarios


def svg_map(input_features, routes, title, status, path, audit_bounds):
    window = box(*audit_bounds)
    geometries = [shape(f["geometry"]).intersection(window) for f in input_features]
    bounds = [g.bounds for g in geometries if not g.is_empty]
    west, south, east, north = (
        min(b[0] for b in bounds),
        min(b[1] for b in bounds),
        max(b[2] for b in bounds),
        max(b[3] for b in bounds),
    )
    sx = 900 / max(east - west, 0.0001)
    sy = 650 / max(north - south, 0.0001)

    def xy(p):
        return f"{65 + (p[0] - west) * sx:.1f},{75 + (north - p[1]) * sy:.1f}"

    def draw(geometry, color, width):
        if geometry.geom_type == "Point":
            x, y = xy(geometry.coords[0]).split(",")
            return f'<circle cx="{x}" cy="{y}" r="6" fill="{color}"/>'
        if geometry.geom_type in ("LineString", "LinearRing"):
            return f'<polyline points="{" ".join(xy(p) for p in geometry.coords)}" fill="none" stroke="{color}" stroke-width="{width}"/>'
        if geometry.geom_type == "Polygon":
            return draw(geometry.exterior, color, width)
        if hasattr(geometry, "geoms"):
            return "".join(draw(g, color, width) for g in geometry.geoms)
        return ""

    items = []
    colors = {
        "Road": "#888",
        "Building": "#993",
        "Marsh": "#827",
        "River": "#18a",
        "Lake": "#18a",
        "BridgeCandidate": "#0a0",
        "GasPipelineCrossingCandidate": "#0aa",
    }
    for f, g in zip(input_features, geometries, strict=True):
        p = f["properties"]
        color = "#d22" if p.get("target") else colors.get(p.get("kind"), "#555")
        if p.get("kind") in (
            "BridgeCandidate",
            "GasPipelineCrossingCandidate",
        ) and not p.get("modelEligible"):
            color = "#aab"
        items.append(draw(g, color, 2.5 if p.get("target") else 1))
    for route in routes:
        items.append(draw(shape(route["geometry"]), "#ef0", 3))
    for f, g in zip(input_features, geometries, strict=True):
        if f["properties"].get("role"):
            items.append(draw(g, "#111", 6))
            x, y = xy(g.coords[0]).split(",")
            items.append(
                f'<text x="{float(x) + 8:.1f}" y="{float(y) - 8:.1f}" font-size="16" font-weight="bold">{f["properties"]["role"]}</text>'
            )
    scale_metres = round(
        100 / (sx / (111195.0802335 * math.cos(math.radians((north + south) / 2))))
    )
    content = f"""<svg xmlns="http://www.w3.org/2000/svg" width="1030" height="800" viewBox="0 0 1030 800"><rect width="1030" height="800" fill="white"/><text x="20" y="30" font-size="18">{title}: {status}</text>{"".join(items)}<path d="M70 750h100" stroke="black" stroke-width="3"/><text x="75" y="745">≈{scale_metres} m</text><text x="200" y="765">Road grey; target red; eligible bridge green/gas cyan; others pale; route yellow; A/B black. © OpenStreetMap contributors</text></svg>"""
    path.write_text(content)


def pr_head_sha():
    event = os.environ.get("GITHUB_EVENT_PATH")
    if event:
        payload = json.loads(Path(event).read_text())
        return payload.get("pull_request", {}).get("head", {}).get(
            "sha"
        ) or os.environ.get("PILOT_PR_HEAD_SHA")
    return os.environ.get("PILOT_PR_HEAD_SHA")


def run(data, verify, output, timeout=120):
    scenarios = json.loads((output / "scenarios.json").read_text())
    results = []
    full_results = []
    coverage = Polygon(json.loads(Path("data/belarus-coverage.json").read_text()))
    for scenario in scenarios:
        folder = output / scenario["id"]
        result_path = folder / "result.json"
        result_path.unlink(missing_ok=True)
        command = [
            "dotnet",
            str(verify),
            "--crossing",
            str(data),
            str(folder / "scenario.json"),
            str(result_path),
        ]
        process = run_bounded_command(
            command, timeout, os.environ | {"DOTNET_GCHeapHardLimit": "60000000"}
        )
        status = "timeout" if process["timeout"] else "error"
        stdout, stderr = process["stdout"], process["stderr"]
        (folder / "stdout.log").write_text(stdout)
        (folder / "stderr.log").write_text(stderr)
        result = (
            json.loads(result_path.read_text())
            if result_path.exists()
            else {
                "status": status or "error",
                "alternatives": [],
                "error": {"reason": "process_failure"},
            }
        )
        result["wallMilliseconds"] = process["elapsed_wall_ms"]
        result["appCommit"] = subprocess.check_output(
            ["git", "rev-parse", "HEAD"], text=True
        ).strip()
        result["sourceDirty"] = bool(
            subprocess.check_output(
                ["git", "status", "--porcelain", "--untracked-files=no"], text=True
            ).strip()
        )
        with verify.open("rb") as stream:
            result["verifyDllSha256"] = hashlib.file_digest(
                stream, "sha256"
            ).hexdigest()
        result["databaseSha256"] = scenario["databaseSha256"]
        source = json.loads((folder / "input.geojson").read_text())
        route_features = []
        findings = []
        raw_features = []
        for f in source["features"]:
            p = f["properties"]
            if "featureId" in p:
                raw_features.append(
                    {
                        "id": p["featureId"],
                        "kind": p["kind"],
                        "geometry": shape(f["geometry"]),
                        "approval_id": p.get("approvalId"),
                    }
                )
        origin = (scenario["points"][0]["Longitude"], scenario["points"][0]["Latitude"])
        for alt in result.get("alternatives", []):
            geometry = LineString(
                [(p["Longitude"], p["Latitude"]) for p in alt["geometry"]]
            )
            checks = audit(
                project(geometry, origin),
                raw_features,
                coverage=project(coverage, origin),
                origin=origin,
            )
            endpoint_ok = (
                distance(
                    tuple(geometry.coords[0]),
                    tuple(
                        (
                            scenario["points"][0]["Longitude"],
                            scenario["points"][0]["Latitude"],
                        )
                    ),
                )
                < 1
                and distance(
                    tuple(geometry.coords[-1]),
                    tuple(
                        (
                            scenario["points"][1]["Longitude"],
                            scenario["points"][1]["Latitude"],
                        )
                    ),
                )
                < 1
            )
            if not endpoint_ok:
                checks["status"] = "violation"
                checks["findings"].append({"reason": "endpoint_mismatch"})
            if not box(*scenario["auditBounds"]).covers(geometry):
                note_incomplete_audit(checks, "outside_audit_window")
            findings.append(checks)
            route_features.append(
                feature(geometry, {"profile": alt["name"], "metrics": alt["metrics"]})
            )
        result["audit"] = {
            "status": "unknown"
            if not route_features
            else "violation"
            if any(f["status"] == "violation" for f in findings)
            else "unknown"
            if any(f["status"] == "unknown" for f in findings)
            else "valid",
            "alternatives": findings,
        }
        save_json(folder / "routes.geojson", geojson(route_features))
        svg_map(
            source["features"],
            route_features,
            scenario["id"],
            result["status"],
            folder / "map.svg",
            scenario["auditBounds"],
        )
        result["id"] = scenario["id"]
        save_json(result_path, result)
        full_results.append(result)
        results.append(
            {
                "id": scenario["id"],
                "category": scenario["category"],
                "distanceMetres": scenario["distanceMetres"],
                "status": result["status"],
                "wallMilliseconds": result["wallMilliseconds"],
                "alternativeCount": len(result.get("alternatives", [])),
                "audit": result["audit"]["status"],
            }
        )
        save_json(
            output / "report.json",
            {
                "schema": "maps.routing-crossing-pilot.v2",
                "prHeadSha": pr_head_sha(),
                "checkoutSha": subprocess.check_output(
                    ["git", "rev-parse", "HEAD"], text=True
                ).strip(),
                "acceptance": pilot_acceptance(scenarios, full_results),
                "appCommit": subprocess.check_output(
                    ["git", "rev-parse", "HEAD"], text=True
                ).strip(),
                "databaseSha256": scenario["databaseSha256"],
                "corpusSha256": hashlib.sha256(
                    (output / "scenarios.json").read_bytes()
                ).hexdigest(),
                "generation": json.loads((output / "generation.json").read_text()),
                "counts": dict(Counter(item["status"] for item in results)),
                "results": results,
            },
        )
        print(scenario["id"], result["status"], flush=True)
    lines = [
        "# Первый эксперимент дорожных пересечений",
        "",
        "Реальные OSM-данные Беларуси; отсутствие маршрута не доказывает физическую невозможность.",
        "",
        "| ID | Категория | Дистанция, м | Результат | Время, мс | Альтернативы | Аудит |",
        "| --- | --- | ---: | --- | ---: | ---: | --- |",
    ]
    for item in results:
        sid = item["id"]
        lines.append(
            f"| [{sid}]({sid}/map.svg) ([GeoJSON]({sid}/input.geojson), [route]({sid}/routes.geojson)) | {item['category']} | {item['distanceMetres']:.0f} | {item['status']} | {item['wallMilliseconds']} | {item['alternativeCount']} | {item['audit']} |"
        )
    lines += [
        "",
        f"Принято: {len(results)}; отказы генератора: {json.loads((output / 'generation.json').read_text())['rejected']}.",
    ]
    counts = Counter(item["status"] for item in results)
    checked = sum(
        item["alternativeCount"] for item in results if item["audit"] == "valid"
    )
    unknown = sum(item["audit"] == "unknown" for item in results)
    lines += [
        "",
        f"Исходный OSM/SQLite SHA-256: `{scenarios[0]['databaseSha256']}`.",
        f"Хеш фиксированного corpus: `{hashlib.sha256((output / 'scenarios.json').read_bytes()).hexdigest()}`.",
        f"Выполнено {len(results)} сценариев: success {counts['success']}, no_route {counts['no_route']}, timeout {counts['timeout']}, resource_limit {counts['resource_limit']}, error {counts['error']}, invalid_input {counts['invalid_input']}.",
        f"Независимый аудит: проверенных допустимых альтернатив {checked}; результатов с неустановленной допустимостью/существованием пути {unknown}.",
        "Отсутствие маршрута и наличие OSM-кандидата не доказывают пропущенный допустимый путь. Инженерные разрешения не проверялись.",
        "",
        "## Случаи для последующего разбора",
        "",
    ]
    lookup = {scenario["id"]: scenario for scenario in scenarios}
    notable = [
        item
        for item in results
        if lookup[item["id"]]["category"] == "A"
        or lookup[item["id"]]["category"] == "F"
        or (item["status"] == "success" and lookup[item["id"]]["category"] == "B")
    ]
    if len(notable) < 3:
        notable.extend(
            item for item in results if item["id"] not in {x["id"] for x in notable}
        )
    for item in notable[:5]:
        case = lookup[item["id"]]
        coords = " → ".join(
            f"({p['Latitude']:.8f}, {p['Longitude']:.8f})" for p in case["points"]
        )
        lines.append(
            f"- **{item['id']}** ({case['category']}, {item['status']}): {coords}; target `{case['targetRoad']['sourceId']}`; кандидаты {case['candidateIds']}. [Карта]({item['id']}/map.svg), [исходные данные]({item['id']}/input.geojson)."
        )
    acceptance = pilot_acceptance(scenarios, full_results)
    lines += [
        "",
        "## Машинная оценка приёмки",
        "",
        "```json",
        json.dumps(acceptance, ensure_ascii=False, indent=2),
        "```",
        "",
        "Harness завершился — это не означает, что критерии пилота выполнены.",
    ]
    lines += [
        "",
        "Для A нужен независимо проверенный witness перед утверждением о пропущенном пути. Контрольные F имеют отдельно сохранённый прямой witness; его вершины не передавались маршрутизатору как пользовательские точки.",
        "",
        "© OpenStreetMap contributors, ODbL 1.0.",
    ]
    (output / "report.md").write_text("\n".join(lines) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("generate", "run"))
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--verify-dll", type=Path)
    parser.add_argument("--corpus-version", choices=("v1", "v2"), default="v2")
    args = parser.parse_args()
    if args.mode == "generate":
        generate(args.data, args.output, corpus_version=args.corpus_version)
    else:
        run(args.data, args.verify_dll, args.output)


if __name__ == "__main__":
    main()
