"""Reproduce independent full-route witness research; this is not an application gate.

Use the verified package and its original routing-regression results.json. Numeric
margins reproduce the research rectangle in the first endpoint's local projection;
they are not the planner's mean-latitude search-margin calculation. Every proposed
full route is checked by the existing audit_alternative with the recorded flags.
"""

import argparse
import importlib
import json
import math
import re
import sqlite3
import sys
import time
from collections import Counter
from contextlib import closing
from heapq import heappop, heappush
from pathlib import Path

import shapely
from shapely.geometry import LineString, Point, Polygon, box, mapping
from shapely.ops import unary_union

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools"))
PILOT = importlib.import_module("routing_crossing_pilot")
REGRESSION = importlib.import_module("routing_regression")
MAX_FEATURES = 8000
MAX_VERTICES = 200000
MAX_TRIANGLES = 300000
BRIDGE_OPENING_METRES = 1.4
GAS_OPENING_METRES = 0.6
BLOCKING = (
    "Road",
    "Building",
    "Marsh",
    "River",
    "Lake",
    "BridgeCandidate",
    "GasPipelineCrossingCandidate",
    "ApprovedCrossing",
)
LIMITATIONS = (
    "Disconnected conservative components do not prove no admissible route exists.",
    "Feature, coordinate and triangle limits are research limits, not impossibility evidence or wall-time guarantees.",
    "Mapped underpasses are never carved into the surface model, even when enabled in the recorded application options.",
    "Numeric margins use the first endpoint's local projection, not natural planner mean-latitude bounds.",
    "Witnesses establish audited model admissibility, not engineering approval or optimal cost.",
    "Geometry order is deterministic for the recorded Shapely/GEOS version; results can differ across versions.",
)


def safe_union(geometries):
    return unary_union(
        [
            geometry if geometry.is_valid else shapely.make_valid(geometry)
            for geometry in geometries
        ]
    )


def polygon_parts(geometry):
    if geometry.geom_type == "Polygon":
        return [geometry]
    return [
        part
        for child in getattr(geometry, "geoms", ())
        for part in polygon_parts(child)
    ]


def triangulated_path(region, start, goal):
    """Hash shared triangle edges instead of constructing all-pairs visibility."""
    triangles = list(shapely.constrained_delaunay_triangles(region).geoms)
    count = len(triangles)
    if count > MAX_TRIANGLES:
        return None, {
            "reason": "triangle_limit",
            "actualCount": count,
            "limit": MAX_TRIANGLES,
        }
    tree = shapely.STRtree(triangles)
    origins = tree.query(Point(start), predicate="intersects")
    destinations = tree.query(Point(goal), predicate="intersects")
    if not len(origins) or not len(destinations):
        return None, {"reason": "endpoint_not_in_mesh"}
    origin, destination = int(min(origins)), int(min(destinations))
    centers = [tuple(triangle.centroid.coords[0]) for triangle in triangles]
    edges = {}
    adjacency = [[] for _ in triangles]
    for index, triangle in enumerate(triangles):
        coordinates = list(triangle.exterior.coords)
        for left, right in zip(coordinates[:-1], coordinates[1:], strict=True):
            key = tuple(sorted((left, right)))
            other = edges.pop(key, None)
            if other is None:
                edges[key] = index
            else:
                midpoint = ((left[0] + right[0]) / 2, (left[1] + right[1]) / 2)
                adjacency[index].append((other, midpoint))
                adjacency[other].append((index, midpoint))
    queue = [(math.dist(centers[origin], goal), 0.0, origin)]
    costs, parents = {origin: 0.0}, {}
    while queue:
        _, cost, current = heappop(queue)
        if current == destination:
            break
        if cost != costs[current]:
            continue
        for neighbor, midpoint in sorted(adjacency[current]):
            next_cost = cost + math.dist(centers[current], centers[neighbor])
            if next_cost < costs.get(neighbor, math.inf):
                costs[neighbor] = next_cost
                parents[neighbor] = (current, midpoint)
                heappush(
                    queue,
                    (
                        next_cost + math.dist(centers[neighbor], goal),
                        next_cost,
                        neighbor,
                    ),
                )
    if destination not in costs:
        return None, {"reason": "mesh_disconnected", "triangles": count}
    portals, current = [], destination
    while current != origin:
        current, midpoint = parents[current]
        portals.append(midpoint)
    raw = [start, *reversed(portals), goal]
    path, cursor = [start], 0
    shapely.prepare(region)
    while cursor < len(raw) - 1:
        best = cursor + 1
        for end in range(min(len(raw) - 1, cursor + 80), cursor + 1, -1):
            if region.covers(LineString([raw[cursor], raw[end]])):
                best = end
                break
        path.append(raw[best])
        cursor = best
    return path, {
        "triangles": count,
        "rawPathVertices": len(raw),
        "pathVertices": len(path),
    }


def search_domain(scenario, record, margin):
    endpoints = [
        (point["Longitude"], point["Latitude"]) for point in scenario["points"]
    ]
    if len(endpoints) != 2 or any(
        not math.isfinite(x)
        or not math.isfinite(y)
        or not -180 <= x <= 180
        or not -90 <= y <= 90
        for x, y in endpoints
    ):
        raise ValueError(
            "A frozen scenario must contain two finite geographic endpoints"
        )
    origin = endpoints[0]
    projected = [
        tuple(PILOT.project(Point(point), origin).coords[0]) for point in endpoints
    ]
    if margin == "mesh":
        mesh = next(
            event["fields"]
            for event in record["diagnostics"]["events"]
            if event.get("eventId") == 5 or event.get("eventName") == "SearchMeshLog"
        )
        bounds = tuple(mesh[key] for key in ("West", "South", "East", "North"))
        domain = PILOT.project(box(*bounds), origin)
    else:
        west, south, east, north = LineString(projected).bounds
        domain = box(west - margin, south - margin, east + margin, north + margin)
        bounds = LineString(
            [PILOT.unproject(x, y, origin) for x, y in domain.exterior.coords]
        ).bounds
    west, south, east, north = bounds
    if not all(math.isfinite(value) for value in bounds) or not (
        -180 <= west < east <= 180 and -90 <= south < north <= 90
    ):
        raise ValueError(
            "Search bounds must be a finite, nonempty geographic rectangle"
        )
    if not all(domain.covers(Point(point)) for point in projected):
        raise ValueError("Recorded search bounds do not contain the original endpoints")
    return endpoints, origin, projected, bounds, domain


def probe_case(
    connection,
    coverage,
    scenario,
    record,
    *,
    margin,
    width,
    max_features=MAX_FEATURES,
    max_vertices=MAX_VERTICES,
):
    """Return an audited witness, an unresolved result, or a structured limit."""
    if type(record.get("effectiveUnderpasses")) is not bool:
        raise ValueError(
            "The run record must contain its actual effectiveUnderpasses boolean"
        )
    if record.get("id") != scenario["id"] or record.get("status") != "no_route":
        raise ValueError(
            "Research inputs must preserve matching original NoRoute scenario IDs"
        )
    if margin != "mesh" and (not math.isfinite(margin) or margin <= 0):
        raise ValueError("A numeric margin must be positive and finite")
    if not math.isfinite(width) or width <= 0 or max_features < 1 or max_vertices < 1:
        raise ValueError("Width and resource limits must be positive")
    started = time.monotonic()
    endpoints, origin, projected_endpoints, bounds, domain = search_domain(
        scenario, record, margin
    )
    result = {
        "id": scenario["id"],
        "originalStatus": record["status"],
        "originalScenario": scenario,
        "effectiveUnderpasses": record["effectiveUnderpasses"],
        "marginMetres": margin,
        "obstacleBufferMetres": width,
        "queryBounds": list(bounds),
        "caps": {
            "maxFeatures": max_features,
            "maxVertices": max_vertices,
            "maxTriangles": MAX_TRIANGLES,
        },
    }
    west, south, east, north = bounds
    placeholders = ",".join("?" for _ in BLOCKING)
    feature_count = connection.execute(
        "SELECT COUNT(*) FROM features f JOIN feature_index i ON i.id=f.id "
        "WHERE i.minx<=? AND i.maxx>=? AND i.miny<=? AND i.maxy>=? "
        f"AND f.kind IN ({placeholders})",
        (east, west, north, south, *BLOCKING),
    ).fetchone()[0]
    if feature_count > max_features:
        return result | {
            "status": "limited",
            "reason": "feature_limit",
            "actualCount": feature_count,
            "limit": max_features,
        }
    features = PILOT.query(connection, bounds, BLOCKING)
    vertices = sum(
        int(shapely.get_num_coordinates(feature["geometry"])) for feature in features
    )
    result.update(
        features=dict(Counter(feature["kind"] for feature in features)),
        rawVertexCount=vertices,
    )
    if vertices > max_vertices:
        return result | {
            "status": "limited",
            "reason": "vertex_limit",
            "actualCount": vertices,
            "limit": max_vertices,
        }
    projected = [
        (feature, PILOT.project(feature["geometry"], origin).intersection(domain))
        for feature in features
    ]
    bridges = safe_union(
        [
            geometry.buffer(BRIDGE_OPENING_METRES, quad_segs=2)
            for feature, geometry in projected
            if feature["kind"] == "BridgeCandidate"
        ]
    )
    approvals = safe_union(
        [
            geometry
            for feature, geometry in projected
            if feature["kind"] == "ApprovedCrossing"
            and (feature.get("approval_id") or "").strip()
        ]
    )
    gas = safe_union(
        [
            geometry.buffer(GAS_OPENING_METRES, quad_segs=2)
            for feature, geometry in projected
            if feature["kind"] == "GasPipelineCrossingCandidate"
        ]
    )
    roads = safe_union(
        [
            geometry.buffer(width, quad_segs=2)
            for feature, geometry in projected
            if feature["kind"] == "Road"
        ]
    ).difference(safe_union([bridges, gas]))
    water = safe_union(
        [
            geometry.buffer(width, quad_segs=2)
            for feature, geometry in projected
            if feature["kind"] in ("River", "Lake")
        ]
    ).difference(safe_union([bridges, approvals]))
    solid = safe_union(
        [
            geometry.buffer(width, quad_segs=2)
            for feature, geometry in projected
            if feature["kind"] in ("Building", "Marsh")
        ]
    )
    parts = polygon_parts(domain.difference(safe_union([roads, water, solid])))
    memberships = [
        [index for index, part in enumerate(parts) if part.covers(Point(point))]
        for point in projected_endpoints
    ]
    shared = set(memberships[0]) & set(memberships[1])
    result.update(
        componentCount=len(parts),
        endpointComponents=memberships,
        connected=bool(shared),
        endpointComponentsTouchQueryBoundary=[
            any(
                parts[index].boundary.intersects(domain.boundary)
                for index in membership
            )
            for membership in memberships
        ],
        status="unresolved",
        reason="conservative_components_disconnected",
    )
    if shared:
        region = parts[min(shared)]
        boundary_vertices = int(shapely.get_num_coordinates(region))
        result.update(
            componentBoundaryVertices=boundary_vertices,
            componentHoles=len(region.interiors),
        )
        if boundary_vertices > max_vertices:
            return result | {
                "status": "limited",
                "reason": "component_vertex_limit",
                "actualCount": boundary_vertices,
                "limit": max_vertices,
            }
        path, diagnostics = triangulated_path(region, *projected_endpoints)
        result.update(diagnostics)
        if path is None:
            result["status"] = (
                "limited" if diagnostics["reason"] == "triangle_limit" else "unresolved"
            )
        else:
            coordinates = [PILOT.unproject(x, y, origin) for x, y in path]
            coordinates[0], coordinates[-1] = endpoints
            alternative = {
                "geometry": [{"Latitude": y, "Longitude": x} for x, y in coordinates]
            }
            checks = REGRESSION.audit_alternative(
                connection,
                coverage,
                scenario,
                alternative,
                enabled=record["effectiveUnderpasses"],
            )
            result.update(
                audit=checks,
                reason="candidate_failed_full_audit",
                candidate=alternative,
            )
            if checks["status"] == "valid":
                result.update(
                    status="witness",
                    reason="full_audit_valid",
                    alternative=result.pop("candidate"),
                )
                result["queriedCrossingSources"] = [
                    {
                        "featureId": feature["id"],
                        "kind": feature["kind"],
                        "sourceId": feature["source_id"],
                        "geometry": mapping(feature["geometry"]),
                    }
                    for feature in features
                    if feature["kind"]
                    in (
                        "BridgeCandidate",
                        "GasPipelineCrossingCandidate",
                        "ApprovedCrossing",
                    )
                ]
    result["elapsedSeconds"] = time.monotonic() - started
    return result


def positive_metres(value):
    try:
        number = float(value)
    except ValueError as error:
        raise argparse.ArgumentTypeError(
            "Expected a positive finite metre value"
        ) from error
    if not math.isfinite(number) or number <= 0:
        raise argparse.ArgumentTypeError("Expected a positive finite metre value")
    return number


def margin_argument(value):
    return value if value == "mesh" else positive_metres(value)


def load_run_report(path, scenarios):
    report = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(report, dict) or not isinstance(report.get("results"), list):
        raise ValueError("Run report must contain a results array")
    if not isinstance(report.get("provenance"), dict):
        raise ValueError("Run report must contain its package and corpus provenance")
    records = {}
    for record in report["results"]:
        if not isinstance(record, dict):
            raise ValueError("Run report results must be objects")
        if record.get("group") != "frozen" or record.get("status") != "no_route":
            continue
        case_id = record.get("id")
        if (
            not isinstance(case_id, str)
            or not re.fullmatch(r"[A-Za-z0-9_-]+", case_id)
            or case_id not in scenarios
            or case_id in records
        ):
            raise ValueError(
                "Frozen NoRoute IDs must be unique, safe corpus identifiers"
            )
        if type(record.get("effectiveUnderpasses")) is not bool:
            raise ValueError(
                "Each frozen NoRoute record must retain its actual effectiveUnderpasses flag"
            )
        records[case_id] = record
    return report, records


def write_json(path, value):
    path.write_text(
        json.dumps(value, ensure_ascii=False, allow_nan=False, indent=2) + "\n",
        encoding="utf-8",
    )


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", required=True, type=Path)
    parser.add_argument("--run-report", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--margin", type=margin_argument, default="mesh")
    parser.add_argument("--width", type=positive_metres, default=0.2)
    parser.add_argument("--ids", nargs="+")
    args = parser.parse_args(argv)
    try:
        corpus = ROOT / "samples/routing-crossing-mass-v1.json"
        receipt = args.data / "package.json"
        receipt_present = receipt.is_file()
        input_paths = {
            "database": args.data / "geodata.sqlite",
            "corpus": corpus,
            "runReport": args.run_report,
            "manifest": args.data / "manifest.json",
            "probe": Path(__file__),
            "routingAuditor": ROOT / "tools/routing_regression.py",
            "overlayAuditor": ROOT / "tools/routing_crossing_pilot.py",
            "coverage": ROOT / "data/belarus-coverage.json",
        }
        if receipt_present:
            input_paths["packageReceipt"] = receipt
        before = REGRESSION.evidence_snapshot(
            {name: path for name, path in input_paths.items() if name != "database"}
        )
        # data_provenance already hashes and verifies the database. Reuse that
        # before hash, then hash it only once more at end-of-batch revalidation.
        metadata = REGRESSION.data_provenance(args.data)
        before["database"] = metadata["databaseSha256"]
        scenarios = {
            scenario["id"]: scenario
            for scenario in json.loads(corpus.read_text(encoding="utf-8"))
        }
        report, records = load_run_report(args.run_report, scenarios)
        selected = sorted(records if args.ids is None else args.ids)
        if len(set(selected)) != len(selected) or any(
            case_id not in records for case_id in selected
        ):
            raise ValueError(
                "--ids must select distinct frozen NoRoute records from this run report"
            )
        expected = metadata | {"corpusSha256": before["corpus"]}
        for key in ("databaseSha256", "sourceSha256", "corpusSha256"):
            if report.get("provenance", {}).get(key) != expected[key]:
                raise ValueError(
                    f"Run report {key} does not match the supplied package and corpus"
                )
        coverage = Polygon(
            json.loads(
                (ROOT / "data/belarus-coverage.json").read_text(encoding="utf-8")
            )
        )
        args.output.mkdir(parents=True, exist_ok=True)
        results = []
        database_uri = (args.data / "geodata.sqlite").resolve().as_uri() + "?mode=ro"
        with closing(sqlite3.connect(database_uri, uri=True)) as connection:
            for case_id in selected:
                (args.output / f"{case_id}-witness.json").unlink(missing_ok=True)
                try:
                    result = probe_case(
                        connection,
                        coverage,
                        scenarios[case_id],
                        records[case_id],
                        margin=args.margin,
                        width=args.width,
                    )
                except (
                    ValueError,
                    KeyError,
                    TypeError,
                    StopIteration,
                    sqlite3.Error,
                    shapely.errors.GEOSException,
                ) as error:
                    result = {
                        "id": case_id,
                        "originalStatus": records[case_id]["status"],
                        "originalScenario": scenarios[case_id],
                        "status": "error",
                        "reason": "probe_error",
                        "errorType": type(error).__name__,
                        "error": str(error),
                    }
                results.append(result)
                print(
                    json.dumps(
                        {
                            "id": case_id,
                            "status": result["status"],
                            "reason": result["reason"],
                        }
                    ),
                    flush=True,
                )
        fatal_error = None
        try:
            REGRESSION.validate_snapshot(input_paths, before)
            if receipt.is_file() != receipt_present:
                raise ValueError("Package receipt presence changed during execution")
        except (OSError, ValueError) as error:
            fatal_error = {
                "reason": "input_changed",
                "errorType": type(error).__name__,
                "error": str(error),
            }
            for result in results:
                result["observedStatus"] = result["status"]
                result["observedReason"] = result["reason"]
                result.update(status="error", reason="input_changed")
                if "alternative" in result:
                    result["candidate"] = result.pop("alternative")
        else:
            for result in results:
                if result["status"] == "witness":
                    write_json(args.output / f"{result['id']}-witness.json", result)
        failed = fatal_error is not None or any(
            result["status"] == "error" for result in results
        )
        write_json(
            args.output / "independent-probe.json",
            {
                "schema": "maps.independent-routing-probe.v1",
                "status": "error" if failed else "complete",
                "fatalError": fatal_error,
                "limitations": LIMITATIONS,
                "provenance": metadata
                | {
                    "corpusSha256": expected["corpusSha256"],
                    "runReportSha256": before["runReport"],
                    "sourceAppCommit": report.get("provenance", {}).get("appCommit"),
                    "probeSha256": before["probe"],
                    "shapelyVersion": shapely.__version__,
                    "geosVersion": shapely.geos_version_string,
                    "auditorSha256": before["routingAuditor"],
                    "overlayAuditorSha256": before["overlayAuditor"],
                    "evidenceHashes": before,
                },
                "provenanceRevalidated": {"unchanged": fatal_error is None},
                "statusCounts": dict(Counter(result["status"] for result in results)),
                "cases": results,
            },
        )
    except (OSError, ValueError, KeyError, TypeError, sqlite3.Error) as error:
        parser.error(str(error))
    return int(failed)


if __name__ == "__main__":
    raise SystemExit(main())
