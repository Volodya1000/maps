"""Run the unchanged frozen 88 routes and explicit controls on a verified package."""

import argparse
import datetime
import hashlib
import json
import math
import os
import platform
import re
import shutil
import sqlite3
import subprocess
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
from pathlib import Path

import shapely
from benchmark_routes import run_bounded_command
from GeodataPreparation.country_data import cache_key
from routing_crossing_pilot import (
    audit,
    distance,
    has_underpass_topology,
    local_road_contact,
    project,
    query,
)
from shapely.geometry import LineString, Point, Polygon
from shapely.ops import unary_union

ROOT = Path(__file__).resolve().parents[1]
CORPUS = ROOT / "samples/routing-crossing-mass-v1.json"
CORPUS_SHA256 = "025262ab54548df41a43b71d230d003151b6e03b2541fec72a9b963e45e0634d"
ENDPOINT_TOLERANCE_METRES = 1
NETWORK_TOLERANCE_METRES = 1
QUERY_MARGIN_DEGREES = 0.001
NO_ROUTE_REASONS = frozenset(
    ("GridConnectionUnavailable", "NoRouteInSearchArea", "RouteLengthLimitExceeded")
)
CONTROLS = (
    ("underpass-on", "routing-underpass-v3/scenario-true.json", "success", True, False),
    (
        "underpass-off",
        "routing-underpass-v3/scenario-false.json",
        "no_route",
        False,
        False,
    ),
    ("victory-300-on", "pr55-network-fix/victory-300-true.json", "success", True, True),
    (
        "victory-300-off",
        "pr55-network-fix/victory-300-false.json",
        "no_route",
        False,
        False,
    ),
    ("footbridge", "pr55-network-fix/bridge.json", "success", False, False),
    ("indirect", "routing-indirect-smolevichi/scenario.json", "success", False, False),
)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def save_json(path, value):
    path.write_text(
        json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )


def underpass_default(options_source):
    matches = re.findall(
        r"\bbool\s+AllowExperimentalPedestrianUnderpasses\s*=\s*(true|false)\b",
        options_source.read_text(encoding="utf-8"),
    )
    if len(matches) != 1:
        raise ValueError("Cannot identify the application underpass default")
    return matches[0] == "true"


def data_provenance(data, *, expected_package_key=None):
    manifest_path = data / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if (
        not isinstance(manifest["schema_version"], int)
        or manifest["schema_version"] < 3
    ):
        raise ValueError("Current country manifest schema >= 3 is required")
    actual = digest(data / "geodata.sqlite")
    if actual != manifest["database_sha256"]:
        raise ValueError("Database SHA-256 differs from the current manifest")
    source_sha = manifest["sources"][0]["sha256"]
    if not re.fullmatch("[0-9a-f]{64}", source_sha):
        raise ValueError("Manifest source SHA-256 is invalid")
    metadata = {
        "databaseSha256": actual,
        "manifestSha256": digest(manifest_path),
        "sourceSha256": source_sha,
        "schemaVersion": manifest["schema_version"],
    }
    if (data / "package.json").exists():
        receipt = json.loads((data / "package.json").read_text(encoding="utf-8"))
        if expected_package_key is not None and receipt["key"] != expected_package_key:
            raise ValueError(
                "Country package converter recipe differs from current inputs"
            )
        if receipt["source_sha256"] != source_sha:
            raise ValueError("Package receipt and manifest source differ")
        if receipt["files"]["manifest.json"] != metadata["manifestSha256"]:
            raise ValueError("Package receipt and manifest checksum differ")
        metadata.update(
            packageKey=receipt["key"],
            packageReceiptSha256=digest(data / "package.json"),
        )
    return metadata


def load_cases(root, corpus):
    from routing_recovery import load_catalogue

    if digest(corpus) != CORPUS_SHA256:
        raise ValueError("Frozen corpus bytes differ from the reviewed 88 scenarios")
    scenarios = json.loads(corpus.read_text(encoding="utf-8"))
    if len(scenarios) != 88 or len({scenario["id"] for scenario in scenarios}) != 88:
        raise ValueError("Expected the 88 distinct frozen scenarios")
    catalogue = load_catalogue(root, corpus)
    recovered = {record["id"] for record in catalogue["records"]}
    catalogue_hash = digest(root / "docs/evidence/routing-recovery/witnesses.json")
    cases = [
        {
            "id": scenario["id"],
            "group": "frozen",
            "scenario": scenario,
            "expected": "success"
            if scenario["quality"] == "verified" or scenario["id"] in recovered
            else "unknown",
            **(
                {"knownWitnessCatalogueSha256": catalogue_hash}
                if scenario["id"] in recovered
                else {}
            ),
            "sourceFile": str(corpus.relative_to(root)),
            "sourceFileSha256": digest(corpus),
        }
        for scenario in scenarios
    ]
    for case_id, source, expected, underpass, network in CONTROLS:
        path = root / "docs/evidence" / source
        cases.append(
            {
                "id": case_id,
                "group": "control",
                "scenario": json.loads(path.read_text(encoding="utf-8")),
                "expected": expected,
                "requireUnderpass": underpass,
                "requireNetwork": network,
                "requireBridgeSource": "way/79123066"
                if case_id == "indirect"
                else None,
                "controlPurpose": "nearby routing smoke, no bridge-use assertion"
                if case_id == "footbridge"
                else expected,
                "sourceFile": str(path.relative_to(root)),
                "sourceFileSha256": digest(path),
            }
        )
    for case in cases:
        if not re.fullmatch(r"[A-Za-z0-9_-]+", case["id"]):
            raise ValueError(
                "Scenario identifiers must be safe artifact directory names"
            )
        if len(case["scenario"]["points"]) != 2:
            raise ValueError("Each frozen input must contain exactly two user points")
    return cases


def topology_features(connection, bounds):
    """Only source-node membership can combine adjacent underground source ways."""
    features = query(connection, bounds)
    networks = {}
    ordinary = []
    for feature in features:
        if feature["kind"] == "PedestrianUnderpassCandidate" and has_underpass_topology(
            feature, feature["geometry"]
        ):
            topology = feature["underpass_topology"]
            member = feature | {
                "nodeIds": topology["nodeIds"],
                "entranceNodeIds": topology["entranceNodeIds"],
            }
            networks.setdefault(topology["networkId"], []).append(member)
        else:
            ordinary.append(feature)
    for network_id, members in networks.items():
        ordinary.append(
            {
                "id": network_id,
                "kind": "PedestrianUnderpassCandidate",
                "geometry": unary_union([member["geometry"] for member in members]),
                "memberIds": [member["id"] for member in members],
                "members": members,
                "underpass_road_ids": sorted(
                    {
                        road
                        for member in members
                        for road in member.get("underpass_road_ids", [])
                    }
                ),
            }
        )
    return ordinary


def mapped_network_walk(route, members, origin, *, require_entrances=False):
    """Follow source edges in route order; geometrically coincident node IDs never merge."""
    edges = []
    entrance_nodes = set()
    node_coordinates = {}
    for member in members:
        coordinates = list(project(member["geometry"], origin).coords)
        nodes = member["nodeIds"]
        entrance_nodes.update(member["entranceNodeIds"])
        if not isinstance(nodes, list) or len(nodes) != len(coordinates):
            return False
        for node, coordinate in zip(nodes, coordinates, strict=True):
            if (
                node in node_coordinates
                and math.dist(node_coordinates[node], coordinate) > 0.001
            ):
                return False
            node_coordinates[node] = coordinate
        for index in range(len(nodes) - 1):
            edge = LineString(coordinates[index : index + 2])
            if edge.length > 0:
                edges.append((nodes[index], nodes[index + 1], edge))
    coordinates = list(route.coords)
    if not edges or len(coordinates) < 2:
        return False
    reachable_edges = set(range(len(edges)))
    if require_entrances:
        reachable_edges = {
            index
            for index, (start, end, edge) in enumerate(edges)
            if any(
                node in entrance_nodes
                and Point(node_coordinates[node]).distance(Point(coordinates[0]))
                <= NETWORK_TOLERANCE_METRES
                for node in (start, end)
            )
        }
    for start_point, end_point in zip(coordinates[:-1], coordinates[1:], strict=True):
        if start_point == end_point:
            continue
        start_point, end_point = Point(start_point), Point(end_point)
        corridor = LineString([start_point, end_point]).buffer(NETWORK_TOLERANCE_METRES)
        adjacency = {}
        start_keys, end_keys = set(), {}
        for edge_index, (start_node, end_node, edge) in enumerate(edges):
            clipped = edge.intersection(corridor)
            pieces = list(clipped.geoms) if hasattr(clipped, "geoms") else [clipped]
            start_distance, end_distance = (
                edge.project(start_point),
                edge.project(end_point),
            )
            for piece in pieces:
                if piece.is_empty or piece.geom_type != "LineString":
                    continue
                low, high = sorted(
                    (
                        edge.project(Point(piece.coords[0])),
                        edge.project(Point(piece.coords[-1])),
                    )
                )
                cuts = [low, high]
                attachments = []
                for point, position, label in (
                    (start_point, start_distance, "start"),
                    (end_point, end_distance, "end"),
                ):
                    if (
                        edge.distance(point) <= NETWORK_TOLERANCE_METRES
                        and low - 1e-7 <= position <= high + 1e-7
                    ):
                        cuts.append(position)
                        attachments.append((position, label))

                def key(
                    position,
                    edge_index=edge_index,
                    start_node=start_node,
                    end_node=end_node,
                    edge=edge,
                ):
                    if position <= 1e-7:
                        return ("node", start_node)
                    if edge.length - position <= 1e-7:
                        return ("node", end_node)
                    return ("edge", edge_index, round(position, 7))

                keys = [key(position) for position in sorted(set(cuts))]
                for left, right in zip(keys[:-1], keys[1:], strict=True):
                    adjacency.setdefault(left, set()).add(right)
                    adjacency.setdefault(right, set()).add(left)
                for position, label in attachments:
                    if label == "start" and edge_index in reachable_edges:
                        start_keys.add(key(position))
                    elif label == "end":
                        end_keys.setdefault(edge_index, set()).add(key(position))
        visited = set(start_keys)
        pending = list(start_keys)
        while pending:
            for node in adjacency.get(pending.pop(), ()):
                if node not in visited:
                    visited.add(node)
                    pending.append(node)
        reachable_edges = {
            edge_index for edge_index, keys in end_keys.items() if keys & visited
        }
        if not reachable_edges:
            return False
    if require_entrances:
        return any(
            node in entrance_nodes
            and Point(node_coordinates[node]).distance(Point(coordinates[-1]))
            <= NETWORK_TOLERANCE_METRES
            for edge_index in reachable_edges
            for node in edges[edge_index][:2]
        )
    return bool(reachable_edges)


def audit_alternative(
    connection, coverage, scenario, alternative, *, enabled, require_network=False
):
    coordinates = [
        (point["Longitude"], point["Latitude"]) for point in alternative["geometry"]
    ]
    if len(coordinates) < 2 or any(
        not math.isfinite(x)
        or not math.isfinite(y)
        or not -180 <= x <= 180
        or not -90 <= y <= 90
        for x, y in coordinates
    ):
        raise ValueError("Returned route coordinates are incomplete or invalid")
    route = LineString(coordinates)
    origin = (scenario["points"][0]["Longitude"], scenario["points"][0]["Latitude"])
    west, south, east, north = route.bounds
    features = topology_features(
        connection,
        (
            west - QUERY_MARGIN_DEGREES,
            south - QUERY_MARGIN_DEGREES,
            east + QUERY_MARGIN_DEGREES,
            north + QUERY_MARGIN_DEGREES,
        ),
    )
    projected = project(route, origin)
    # The shared geometry audit needs original source ways and topology. Grouped
    # network geometries remain available for the research source-walk gates.
    source_features = [
        member for feature in features for member in feature.get("members", [feature])
    ]
    checks = audit(
        projected,
        source_features,
        project(coverage, origin),
        origin,
        allow_underpasses=enabled,
    )
    sources = {feature["id"]: feature.get("source_id") for feature in source_features}
    for finding in checks["findings"]:
        feature_id = finding.get("featureId", finding.get("roadId"))
        if feature_id in sources:
            finding["sourceId"] = sources[feature_id]
        if finding.get("underpassIds"):
            road = next(
                feature for feature in features if feature["id"] == finding["roadId"]
            )
            road_geometry = project(road["geometry"], origin)
            point = Point(finding["coordinate"])
            section = local_road_contact(projected, road_geometry, point)
            parts = list(section.geoms) if hasattr(section, "geoms") else [section]
            allowed = []
            for network in (
                feature
                for feature in features
                if set(feature.get("memberIds", [])) & set(finding["underpassIds"])
            ):
                members = network.get("members", [])
                linked_contact = any(
                    finding["roadId"] in member.get("underpass_road_ids", [])
                    and project(member["geometry"], origin).distance(point)
                    <= NETWORK_TOLERANCE_METRES
                    and project(member["geometry"], origin).intersects(road_geometry)
                    for member in members
                )
                if (
                    linked_contact
                    and parts
                    and all(
                        mapped_network_walk(part, members, origin) for part in parts
                    )
                ):
                    allowed.append(network["id"])
            finding["underpassIds"] = allowed
            finding["allowed"] = bool(
                allowed or finding["bridgeIds"] or finding["gasIds"]
            )
            if not finding["allowed"]:
                checks["status"] = "violation"
                checks["findings"].append(
                    {"reason": "underpass_topology_jump", "roadId": finding["roadId"]}
                )
    endpoint_distances = [
        distance(actual, (expected["Longitude"], expected["Latitude"]))
        for actual, expected in zip(
            (coordinates[0], coordinates[-1]), scenario["points"], strict=True
        )
    ]
    checks["endpointErrorsMetres"] = endpoint_distances
    checks["routeBounds"] = list(route.bounds)
    length = sum(
        distance(left, right)
        for left, right in zip(coordinates[:-1], coordinates[1:], strict=True)
    )
    checks["computedLengthMetres"] = length
    if length > scenario["routingOptions"].get("MaxRouteLengthMetres", math.inf):
        checks["status"] = "violation"
        checks["findings"].append(
            {
                "reason": "route_length_limit",
                "actualMetres": length,
                "limitMetres": scenario["routingOptions"]["MaxRouteLengthMetres"],
            }
        )
    checks["bridgeSourceIds"] = sorted(
        {
            sources[bridge_id]
            for finding in checks["findings"]
            for bridge_id in finding.get("bridgeIds", [])
            if sources.get(bridge_id)
        }
    )
    if any(error >= ENDPOINT_TOLERANCE_METRES for error in endpoint_distances):
        checks["status"] = "violation"
        checks["findings"].append(
            {"reason": "endpoint_mismatch", "errorsMetres": endpoint_distances}
        )
    if require_network:
        network = unary_union(
            [
                project(feature["geometry"], origin)
                for feature in features
                if feature["kind"] == "PedestrianUnderpassCandidate"
            ]
        )
        checks["entireRouteInsideMappedNetwork1m"] = network.buffer(
            NETWORK_TOLERANCE_METRES
        ).covers(projected)
        if not checks["entireRouteInsideMappedNetwork1m"]:
            checks["status"] = "violation"
            checks["findings"].append({"reason": "outside_mapped_underpass_network"})
        checks["mappedSourceNodeWalk"] = any(
            mapped_network_walk(
                projected, feature.get("members", []), origin, require_entrances=True
            )
            for feature in features
            if feature["kind"] == "PedestrianUnderpassCandidate"
        )
        if not checks["mappedSourceNodeWalk"]:
            if checks["status"] == "unknown" and any(
                finding.get("missingUnderpassTopologyIds")
                for finding in checks["findings"]
            ):
                checks["findings"].append({"reason": "underpass_topology_incomplete"})
            else:
                checks["status"] = "violation"
                checks["findings"].append({"reason": "underpass_topology_jump"})
    return checks


def search_meshes(snapshot):
    """Validate ordered mesh attempts; retain one legacy mesh without Attempt."""
    if not isinstance(snapshot, dict) or snapshot.get("droppedEvents") != 0:
        return None
    events = snapshot.get("events")
    if not isinstance(events, list):
        return None
    meshes = [
        event.get("fields")
        for event in events
        if isinstance(event, dict) and event.get("eventId") == 5
    ]
    if not meshes or any(not isinstance(mesh, dict) for mesh in meshes):
        return None
    legacy = len(meshes) == 1 and "Attempt" not in meshes[0]
    calculation_id = meshes[0].get("CalculationId")
    for attempt, mesh in enumerate(meshes):
        if not legacy and (
            type(mesh.get("Attempt")) is not int
            or mesh["Attempt"] != attempt
            or not isinstance(calculation_id, str)
            or not calculation_id
            or mesh.get("CalculationId") != calculation_id
        ):
            return None
        bounds = [mesh.get(key) for key in ("West", "South", "East", "North")]
        if any(
            type(value) not in (int, float) or not math.isfinite(value)
            for value in bounds
        ):
            return None
        west, south, east, north = bounds
        if not (-180 <= west < east <= 180 and -90 <= south < north <= 90):
            return None
    return meshes


def complete_diagnostics(snapshot, status, no_route_reason=None):
    if not isinstance(snapshot, dict) or snapshot.get("droppedEvents") != 0:
        return False
    events = snapshot.get("events")
    if not isinstance(events, list) or not events:
        return False
    meshes = search_meshes(snapshot)
    if meshes is None:
        return False
    calculation_id = meshes[0].get("CalculationId")
    required = {
        5: ("NodeCount", "Columns", "Rows", "PortalCount"),
        6: (
            "LegIndex",
            "StartConnections",
            "DestinationConnections",
            "PortalNodeCount",
            "PortalGridConnections",
            "PortalEdges",
            "RejectedConnections",
        ),
        7: ("LegIndex", "ExpandedNodes", "EdgeEvaluations"),
    }
    observed = set()
    outcome_matches = False
    for event in events:
        if not isinstance(event, dict) or not isinstance(event.get("fields"), dict):
            return False
        event_id, fields = event.get("eventId"), event["fields"]
        if (
            event_id in (5, 6, 7, 8, 9)
            and fields.get("CalculationId") != calculation_id
        ):
            return False
        if event_id == 8:
            if (
                type(fields.get("Attempt")) is not int
                or not 0 <= fields["Attempt"] < len(meshes)
                or type(fields.get("ConnectionRadiusCells")) is not int
                or fields["ConnectionRadiusCells"] <= 0
                or type(fields.get("SurfaceRecovery")) is not bool
            ):
                return False
        if event_id == 9:
            if (
                any(
                    type(fields.get(key)) is not int or fields[key] < 0
                    for key in (
                        "LegIndex",
                        "NodeCount",
                        "ExpandedNodes",
                        "InputVertexCount",
                    )
                )
                or any(
                    type(fields.get(key)) is not bool
                    for key in ("ResourceLimited", "Found")
                )
                or not isinstance(fields.get("Profile"), str)
                or not fields["Profile"]
            ):
                return False
        if event_id not in required:
            continue
        if not fields.get("CalculationId") or any(
            type(fields.get(key)) is not int or fields[key] < 0
            for key in required[event_id]
        ):
            return False
        if event_id == 5:
            outcome_matches = False
            numeric = (
                "CellSizeMetres",
                "RequestedCellSizeMetres",
                "West",
                "South",
                "East",
                "North",
            )
            if any(
                type(fields.get(key)) not in (int, float)
                or not math.isfinite(fields[key])
                for key in numeric
            ):
                return False
            if (
                fields["CellSizeMetres"] <= 0
                or fields["RequestedCellSizeMetres"] <= 0
                or fields["East"] <= fields["West"]
                or fields["North"] <= fields["South"]
            ):
                return False
        else:
            if not isinstance(fields.get("Profile"), str) or not fields["Profile"]:
                return False
            booleans = (
                ("DirectConnection",) if event_id == 6 else ("Cancelled", "DirectRoute")
            )
            if any(type(fields.get(key)) is not bool for key in booleans):
                return False
            if event_id == 7:
                reason = fields.get("Reason")
                if reason is not None and not isinstance(reason, str):
                    return False
                outcome_matches |= not fields["Cancelled"] and (
                    (status == "success" and reason is None)
                    or (
                        status == "no_route"
                        and (
                            reason in NO_ROUTE_REASONS
                            or (
                                no_route_reason == "RouteLengthLimitExceeded"
                                and reason is None
                            )
                        )
                    )
                )
        observed.add(event_id)
    return observed == set(required) and outcome_matches


def complete_no_route_error(error):
    return (
        isinstance(error, dict)
        and error.get("reason") in NO_ROUTE_REASONS
        and isinstance(error.get("searchFailures"), list)
        and bool(error["searchFailures"])
        and all(
            isinstance(failure, dict)
            and isinstance(failure.get("profile"), str)
            and bool(failure["profile"])
            and failure.get("reason") in NO_ROUTE_REASONS
            for failure in error["searchFailures"]
        )
    )


def run_case(
    case,
    connection,
    coverage,
    data,
    verify,
    output,
    *,
    default_underpasses,
    timeout_seconds=120,
    command_runner=run_bounded_command,
):
    folder = output / case["group"] / case["id"]
    folder.mkdir(parents=True, exist_ok=True)
    scenario = case["scenario"]
    save_json(folder / "original-scenario.json", scenario)
    # Diagnostics are a Verify-only opt-in; points and routing options stay verbatim.
    save_json(folder / "scenario.json", scenario | {"includeDiagnostics": True})
    result_path = folder / "result.json"
    result_path.unlink(missing_ok=True)
    enabled = scenario["routingOptions"].get(
        "AllowExperimentalPedestrianUnderpasses", default_underpasses
    )
    record = {key: value for key, value in case.items() if key != "scenario"}
    record.update(
        status="error",
        classification="technical_failure",
        gatePassed=False,
        auditStatus="unknown",
        audits=[],
        effectiveUnderpasses=enabled,
        rawResult=str(result_path.relative_to(output)),
        originalScenarioSha256=digest(folder / "original-scenario.json"),
        invocationSha256=digest(folder / "scenario.json"),
        originalDatabaseSha256=scenario.get(
            "databaseSha256", scenario.get("sourceDatabaseSha256")
        ),
    )
    command = [
        "dotnet",
        str(verify),
        "--crossing",
        str(data),
        str(folder / "scenario.json"),
        str(result_path),
    ]
    try:
        process = command_runner(
            command,
            timeout_seconds,
            os.environ | {"DOTNET_GCHeapHardLimit": "60000000"},
        )
        (folder / "stdout.log").write_text(process["stdout"], encoding="utf-8")
        (folder / "stderr.log").write_text(process["stderr"], encoding="utf-8")
        record["process"] = {
            key: value
            for key, value in process.items()
            if key not in ("stdout", "stderr")
        }
        save_json(folder / "process.json", record["process"] | {"command": command})
        if process["timeout"] or process["returncode"] != 0:
            record["failureReason"] = (
                "process_timeout" if process["timeout"] else "process_failure"
            )
        elif not result_path.exists():
            record["failureReason"] = "missing_result"
        else:
            result = json.loads(result_path.read_text(encoding="utf-8"))
            status = result["status"]
            alternatives = result["alternatives"]
            record.update(
                status=status,
                error=result.get("error"),
                diagnostics=result.get("diagnostics"),
            )
            if (
                status not in ("success", "no_route")
                or not isinstance(alternatives, list)
                or (status == "success") != bool(alternatives)
                or (
                    status == "no_route"
                    and not complete_no_route_error(result.get("error"))
                )
            ):
                record["failureReason"] = "inconsistent_result"
            elif (
                case["group"] != "baseline" or result.get("diagnostics") is not None
            ) and not complete_diagnostics(
                result.get("diagnostics"),
                status,
                (result.get("error") or {}).get("reason"),
            ):
                record["failureReason"] = "incomplete_diagnostics"
            elif status == "no_route":
                direct = audit_alternative(
                    connection,
                    coverage,
                    scenario,
                    {"geometry": scenario["points"]},
                    enabled=enabled,
                )
                direct["meaning"] = (
                    "Straight-line evidence only; a blocked line does not rule out a detour"
                )
                direct["withinRouteLengthLimit"] = distance(
                    (
                        scenario["points"][0]["Longitude"],
                        scenario["points"][0]["Latitude"],
                    ),
                    (
                        scenario["points"][1]["Longitude"],
                        scenario["points"][1]["Latitude"],
                    ),
                ) <= scenario["routingOptions"].get("MaxRouteLengthMetres", math.inf)
                record["straightLineDiagnostic"] = direct
                record.update(
                    classification="unknown", gatePassed=case["expected"] != "success"
                )
                if not record["gatePassed"]:
                    record["failureReason"] = "known_positive_missing"
                elif direct["status"] == "valid" and direct["withinRouteLengthLimit"]:
                    record.update(
                        gatePassed=False,
                        classification="missed_valid_path",
                        failureReason="valid_direct_witness_missed",
                    )
            else:
                record["audits"] = [
                    audit_alternative(
                        connection,
                        coverage,
                        scenario,
                        alternative,
                        enabled=enabled,
                        require_network=case.get("requireNetwork", False),
                    )
                    for alternative in alternatives
                ]
                statuses = {check["status"] for check in record["audits"]}
                record["auditStatus"] = (
                    "violation"
                    if "violation" in statuses
                    else "unknown"
                    if "unknown" in statuses
                    else "valid"
                )
                record.update(
                    classification="valid_route"
                    if statuses == {"valid"}
                    else "invalid_route",
                    gatePassed=statuses == {"valid"},
                    alternativeCount=len(alternatives),
                )
                if not record["gatePassed"]:
                    record["failureReason"] = "route_audit_not_valid"
                if case["expected"] == "no_route":
                    record.update(
                        gatePassed=False,
                        failureReason="negative_control_returned_route",
                    )
                if case.get("requireUnderpass"):
                    used = any(
                        finding.get("underpassIds")
                        for check in record["audits"]
                        for finding in check["findings"]
                    )
                    record["flagOffAudits"] = [
                        audit_alternative(
                            connection, coverage, scenario, alternative, enabled=False
                        )
                        for alternative in alternatives
                    ]
                    if not used or not any(
                        check["status"] == "violation"
                        for check in record["flagOffAudits"]
                    ):
                        record.update(
                            gatePassed=False,
                            failureReason="underpass_control_not_exercised",
                        )
                if case.get("requireBridgeSource") and not any(
                    case["requireBridgeSource"] in check["bridgeSourceIds"]
                    for check in record["audits"]
                ):
                    record.update(
                        gatePassed=False, failureReason="bridge_control_not_exercised"
                    )
    except (
        OSError,
        ValueError,
        KeyError,
        TypeError,
        sqlite3.Error,
        shapely.errors.GEOSException,
    ) as error:
        record.update(
            gatePassed=False,
            classification="technical_failure",
            failureReason="evidence_error",
            evidenceError={"type": type(error).__name__, "detail": str(error)},
        )
    save_json(folder / "audit.json", record)
    return record


def write_reports(report, output):
    output.mkdir(parents=True, exist_ok=True)
    results = report["results"]
    report["counts"] = {
        key: sum(result["classification"] == key for result in results)
        for key in (
            "valid_route",
            "unknown",
            "invalid_route",
            "technical_failure",
            "missed_valid_path",
        )
    }
    report["statusCounts"] = dict(Counter(result["status"] for result in results))
    complete = all(
        len([result for result in results if result["group"] == group]) == report[key]
        for group, key in (
            ("frozen", "expectedFrozenCount"),
            ("control", "expectedControlCount"),
        )
        if key in report
    )
    if "expectedFrozenCount" in report:
        complete = (
            complete
            and report.get("provenanceRevalidated", {}).get("unchanged") is True
        )
    baseline_results = report.get("baselineResults", [])
    baseline_complete = not report.get("samePackageBaseline") or len(
        baseline_results
    ) == report.get("expectedFrozenCount", 88)
    report["gatePassed"] = (
        bool(results)
        and complete
        and baseline_complete
        and not report.get("fatalError")
        and all(result["gatePassed"] for result in results)
        and all(
            result["gatePassed"] or result["classification"] == "missed_valid_path"
            for result in baseline_results
        )
    )
    save_json(output / "results.json", report)
    counts = report["counts"]
    lines = [
        "# Frozen routing regression",
        "",
        f"Gate: **{'PASS' if report['gatePassed'] else 'FAIL'}**",
        "",
        f"Valid routes: {counts['valid_route']}; unknown NoRoute: {counts['unknown']}; "
        f"invalid routes: {counts['invalid_route']}; technical failures: {counts['technical_failure']}; "
        f"missed direct witnesses: {counts['missed_valid_path']}.",
        "",
        "Exploratory NoRoute remains unknown; it is not proof of physical impossibility.",
        "",
    ]
    for key, value in report.get("provenance", {}).items():
        if key in (
            "appCommit",
            "databaseSha256",
            "verifyDllSha256",
            "corpusSha256",
            "effectiveDefaultUnderpasses",
        ):
            lines.append(f"- {key}: `{value}`")
    if report.get("historicalBaseline"):
        lines += [
            "",
            "Historical baseline uses its own recorded binary/data/options; it is not current execution evidence.",
        ]
    if report.get("samePackageBaseline"):
        baseline = report["samePackageBaseline"]
        lines += [
            "",
            f"Same-package baseline commit: `{baseline['appCommit']}`; binary SHA-256: `{baseline['verifyDllSha256']}`.",
            f"Lost valid baseline routes: {sum(item['lostValidRoute'] for item in report.get('transitions', []))}.",
        ]
    if report.get("fatalError"):
        lines += [
            "",
            f"Evidence setup failed: {report['fatalError']['type']}: {report['fatalError']['detail']}",
        ]
    lines += [
        "",
        "| Group | Scenario | Status | Audit | Gate | Historical status | Reason |",
        "| --- | --- | --- | --- | --- | --- | --- |",
    ]
    for result in results:
        lines.append(
            f"| {result['group']} | {result['id']} | {result['status']} | {result['auditStatus']} | "
            f"{'PASS' if result['gatePassed'] else 'FAIL'} | {result.get('historicalStatus', '')} | {result.get('failureReason', '')} |"
        )
    (output / "summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def compare_baseline(results, baseline_results):
    current = {
        result["id"]: result for result in results if result["group"] == "frozen"
    }
    baseline = {result["id"]: result for result in baseline_results}
    if (
        len(current) != sum(result["group"] == "frozen" for result in results)
        or len(baseline) != len(baseline_results)
        or set(current) != set(baseline)
    ):
        raise ValueError("Same-package baseline inventory differs from the frozen run")
    transitions = []
    for case_id, result in current.items():
        before = baseline[case_id]
        lost = (
            before["status"] == "success"
            and before["auditStatus"] == "valid"
            and (result["status"] != "success" or result["auditStatus"] != "valid")
        )
        transitions.append(
            {
                "id": case_id,
                "baselineStatus": before["status"],
                "currentStatus": result["status"],
                "baselineAuditStatus": before["auditStatus"],
                "currentAuditStatus": result["auditStatus"],
                "lostValidRoute": lost,
            }
        )
        result["baselineStatus"] = before["status"]
        if lost:
            result.update(gatePassed=False, failureReason="baseline_valid_route_lost")
    return transitions


def binary_provenance(verify):
    return {
        "verifyDllSha256": digest(verify),
        "binaryFiles": {
            path.relative_to(verify.parent).as_posix(): digest(path)
            for path in sorted(verify.parent.rglob("*"))
            if path.is_file()
        },
    }


def evidence_snapshot(paths):
    return {name: digest(path) for name, path in paths.items()}


def validate_snapshot(paths, before):
    after = evidence_snapshot(paths)
    changed = sorted(
        name for name in set(before) | set(after) if before.get(name) != after.get(name)
    )
    if changed:
        raise ValueError(
            "Evidence inputs changed during execution: " + ", ".join(changed)
        )


def baseline_provenance(verify, source):
    if source is None:
        source = next(
            (
                parent
                for parent in verify.resolve().parents
                if (
                    parent / "src/CableRoutePlanner.Application/RoutingOptions.cs"
                ).exists()
            ),
            None,
        )
    if source is None:
        raise ValueError(
            "Pass --baseline-source for a baseline binary outside its checkout"
        )
    commit = subprocess.check_output(
        ["git", "-C", str(source), "rev-parse", "HEAD"], text=True
    ).strip()
    dirty = subprocess.check_output(
        ["git", "-C", str(source), "status", "--porcelain", "--untracked-files=no"],
        text=True,
    ).strip()
    return binary_provenance(verify) | {
        "appCommit": commit,
        "sourceDirty": bool(dirty),
        "sourceRoot": str(source.resolve()),
        "effectiveDefaultUnderpasses": underpass_default(
            source / "src/CableRoutePlanner.Application/RoutingOptions.cs"
        ),
    }


def code_provenance(root, verify, corpus):
    def git(*arguments):
        return subprocess.check_output(
            ["git", "-C", str(root), *arguments], text=True
        ).strip()

    sources = {
        name: digest(root / name)
        for name in git("ls-files").splitlines()
        if Path(name).suffix in (".cs", ".csproj", ".props", ".targets", ".py")
        and (root / name).is_file()
    }
    sources["tools/routing_regression.py"] = digest(Path(__file__))
    for name in (
        "tools/routing_recovery.py",
        "tools/routing_portal_witnesses.py",
        "docs/evidence/routing-recovery/witnesses.json",
    ):
        sources[name] = digest(root / name)
    return {
        "appCommit": git("rev-parse", "HEAD"),
        "sourceDirty": bool(git("status", "--porcelain")),
        "sourceTreeSha256": hashlib.sha256(
            json.dumps(sources, sort_keys=True).encode()
        ).hexdigest(),
        "sourceFiles": sources,
        **binary_provenance(verify),
        "corpusSha256": digest(corpus),
        "knownWitnessCatalogueSha256": digest(
            root / "docs/evidence/routing-recovery/witnesses.json"
        ),
        "effectiveDefaultUnderpasses": underpass_default(
            root / "src/CableRoutePlanner.Application/RoutingOptions.cs"
        ),
        "workflowHeadSha": os.environ.get("ROUTING_PR_HEAD_SHA"),
        "workflowRunId": os.environ.get("GITHUB_RUN_ID"),
        "workflowRunAttempt": os.environ.get("GITHUB_RUN_ATTEMPT"),
        "platform": platform.platform(),
        "pythonVersion": platform.python_version(),
        "shapelyVersion": shapely.__version__,
    }


def run_cases(
    cases,
    data,
    verify,
    output,
    coverage,
    *,
    default_underpasses,
    timeout_seconds,
    workers=1,
):
    """Keep each process/audit isolated, yielding evidence in frozen input order."""

    def execute(case):
        with closing(
            sqlite3.connect(
                (data / "geodata.sqlite").resolve().as_uri() + "?mode=ro", uri=True
            )
        ) as connection:
            return run_case(
                case,
                connection,
                coverage,
                data,
                verify,
                output,
                default_underpasses=default_underpasses,
                timeout_seconds=timeout_seconds,
            )

    with ThreadPoolExecutor(max_workers=workers) as executor:
        yield from executor.map(execute, cases)


def run_regression(args):
    args.output.mkdir(parents=True, exist_ok=True)
    report = {
        "schema": "maps.routing-regression.v1",
        "recordedAt": datetime.datetime.now(datetime.UTC).isoformat(),
        "provenance": {},
        "results": [],
        "timeoutSeconds": args.timeout_seconds,
        "workers": args.workers,
        "expectedFrozenCount": 88,
        "expectedControlCount": len(CONTROLS),
    }
    write_reports(report, args.output)
    try:
        cases = load_cases(ROOT, args.corpus)
        lock = ROOT / "tools/GeodataPreparation/belarus.lock.json"
        lock_data = json.loads(lock.read_text())
        provenance = code_provenance(
            ROOT, args.verify_dll, args.corpus
        ) | data_provenance(args.data, expected_package_key=cache_key(lock_data, ROOT))
        report["provenance"] = provenance
        if provenance["sourceSha256"] != lock_data["source"]["sha256"]:
            raise ValueError(
                "Country source differs from the repository's pinned source"
            )
        for source, destination in (
            (args.corpus, "frozen-corpus.json"),
            (lock, "country.lock.json"),
            (args.data / "manifest.json", "data-manifest.json"),
        ):
            shutil.copyfile(source, args.output / destination)
        if (args.data / "package.json").exists():
            shutil.copyfile(
                args.data / "package.json", args.output / "data-package.json"
            )
        baseline_path = ROOT / "docs/evidence/routing-mass-v1/results.json"
        baseline = json.loads(baseline_path.read_text(encoding="utf-8"))
        historical = {result["id"]: result["status"] for result in baseline["results"]}
        if set(historical) != {
            case["id"] for case in cases if case["group"] == "frozen"
        }:
            raise ValueError("Historical baseline scenario inventory differs")
        report["historicalBaseline"] = {
            key: baseline[key]
            for key in (
                "baselineAppCommit",
                "databaseSha256",
                "verifyDllSha256",
                "corpusSha256",
            )
        }
        report["historicalBaseline"]["reportSha256"] = digest(baseline_path)
        coverage = Polygon(
            json.loads((ROOT / "data/belarus-coverage.json").read_text())
        )
        if args.baseline_verify_dll:
            baseline = baseline_provenance(
                args.baseline_verify_dll, args.baseline_source
            )
            baseline["databaseSha256"] = provenance["databaseSha256"]
            report["samePackageBaseline"] = baseline
            report["baselineResults"] = []
        input_paths = {
            "corpus": args.corpus,
            "database": args.data / "geodata.sqlite",
            "manifest": args.data / "manifest.json",
            "countryLock": lock,
            "coverage": ROOT / "data/belarus-coverage.json",
            "historicalBaseline": baseline_path,
            "knownWitnessCatalogue": ROOT
            / "docs/evidence/routing-recovery/witnesses.json",
        }
        from routing_recovery import load_catalogue

        input_paths.update(
            {
                f"knownWitnessFixture/{record['id']}": ROOT / record["fixture"]
                for record in load_catalogue(ROOT, args.corpus)["records"]
            }
        )
        if (args.data / "package.json").exists():
            input_paths["packageReceipt"] = args.data / "package.json"
        input_paths.update(
            {
                f"control/{case['id']}": ROOT / case["sourceFile"]
                for case in cases
                if case["group"] == "control"
            }
        )
        input_paths.update(
            {f"source/{name}": ROOT / name for name in provenance["sourceFiles"]}
        )
        input_paths.update(
            {
                f"binary/{name}": args.verify_dll.parent / name
                for name in provenance["binaryFiles"]
            }
        )
        if args.baseline_verify_dll:
            input_paths.update(
                {
                    f"baselineBinary/{name}": args.baseline_verify_dll.parent / name
                    for name in baseline["binaryFiles"]
                }
            )
            input_paths["baselineOptions"] = (
                Path(baseline["sourceRoot"])
                / "src/CableRoutePlanner.Application/RoutingOptions.cs"
            )
        provenance["evidenceHashes"] = evidence_snapshot(input_paths)
        if args.baseline_verify_dll:
            baseline_cases = [
                case | {"group": "baseline", "expected": "unknown"}
                for case in cases
                if case["group"] == "frozen"
            ]
            for record in run_cases(
                baseline_cases,
                args.data,
                args.baseline_verify_dll,
                args.output,
                coverage,
                default_underpasses=baseline["effectiveDefaultUnderpasses"],
                timeout_seconds=args.timeout_seconds,
                workers=args.workers,
            ):
                report["baselineResults"].append(record)
                write_reports(report, args.output)
                print(
                    f"baseline/{record['id']}: {record['status']}, {record['auditStatus']}",
                    flush=True,
                )
        for result in run_cases(
            cases,
            args.data,
            args.verify_dll,
            args.output,
            coverage,
            default_underpasses=provenance["effectiveDefaultUnderpasses"],
            timeout_seconds=args.timeout_seconds,
            workers=args.workers,
        ):
            if result["group"] == "frozen":
                result["historicalStatus"] = historical[result["id"]]
            report["results"].append(result)
            write_reports(report, args.output)
            print(
                f"{result['group']}/{result['id']}: {result['status']}, {result['auditStatus']}, gate={result['gatePassed']}",
                flush=True,
            )
        if args.baseline_verify_dll:
            report["transitions"] = compare_baseline(
                report["results"], report["baselineResults"]
            )
        validate_snapshot(input_paths, provenance["evidenceHashes"])
        report["provenanceRevalidated"] = {
            "unchanged": True,
            "checkedFiles": len(input_paths),
        }
    except (
        OSError,
        ValueError,
        KeyError,
        TypeError,
        sqlite3.Error,
        subprocess.CalledProcessError,
    ) as error:
        report["fatalError"] = {"type": type(error).__name__, "detail": str(error)}
    write_reports(report, args.output)
    return 0 if report["gatePassed"] else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--verify-dll", type=Path, required=True)
    parser.add_argument("--baseline-verify-dll", type=Path)
    parser.add_argument("--baseline-source", type=Path)
    parser.add_argument("--corpus", type=Path, default=CORPUS)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--timeout-seconds", type=float, default=120)
    parser.add_argument("--workers", type=int, choices=range(1, 17), default=1)
    args = parser.parse_args()
    if not math.isfinite(args.timeout_seconds) or args.timeout_seconds <= 0:
        parser.error("--timeout-seconds must be finite and positive")
    return run_regression(args)


if __name__ == "__main__":
    raise SystemExit(main())
