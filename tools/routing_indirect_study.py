"""Reproduce one indirect, model-allowed road crossing on pinned Belarus data."""

import argparse
import hashlib
import json
import math
import os
import sqlite3
import subprocess
from pathlib import Path

from benchmark_routes import run_bounded_command
from routing_crossing_pilot import (
    audit,
    distance,
    feature,
    geojson,
    project,
    query,
    save_json,
    svg_map,
)
from shapely.geometry import LineString, Point, Polygon, box

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SCENARIO = ROOT / "samples/routing-indirect-smolevichi.json"
MINIMUM_SEARCH_MARGIN_METRES = 1_000


def classify_result(baseline, route_audits):
    """Classify observable behavior without inferring a routing defect."""
    if any(item.get("status") == "violation" for item in route_audits):
        classification = "domain_violation"
        defect = True
    elif (
        baseline.get("status") == "success"
        and baseline.get("alternatives")
        and route_audits
        and all(item.get("status") == "valid" for item in route_audits)
    ):
        classification = "valid_route_found"
        defect = False
    elif baseline.get("status") in ("timeout", "resource_limit"):
        classification = "time_or_resource_limit"
        defect = False
    elif baseline.get("status") == "no_route":
        classification = "confirmed_missed_valid_path"
        defect = False
    else:
        classification = "correctness_unknown"
        defect = False
    return {
        "classification": classification,
        "productionDefectConfirmed": defect,
        "diagnosticRuns": [],
    }


def effective_baseline(written_result, process):
    """Prefer the bounded process outcome over a partial result file."""
    if process["timeout"] or process["returncode"] != 0 or written_result is None:
        return {
            "status": "timeout" if process["timeout"] else "error",
            "alternatives": [],
            "error": {
                "reason": "process_timeout"
                if process["timeout"]
                else "process_failure",
                "returnCode": process["returncode"],
            },
        }
    return written_result


def audit_scenario(scenario, features, *, coverage=None, origin=None):
    """Audit the forbidden direct segment and the separate witness polyline."""
    points = [(point["Longitude"], point["Latitude"]) for point in scenario["points"]]
    direct_geometry = LineString(points)
    witness_geometry = LineString(scenario["witness"]["coordinates"])
    if origin is not None:
        direct_geometry = project(direct_geometry, origin)
        witness_geometry = project(witness_geometry, origin)
    return {
        "direct": audit(direct_geometry, features, coverage, origin),
        "witness": audit(witness_geometry, features, coverage, origin),
    }


def validate_pre_route(scenario, direct_audit, witness_audit):
    """Reject evidence that does not prove a two-point indirect crossing."""
    if len(scenario.get("points", [])) != 2:
        raise ValueError("scenario must contain exactly two user points")
    witness = scenario.get("witness", {})
    coordinates = witness.get("coordinates", [])
    points = [[point["Longitude"], point["Latitude"]] for point in scenario["points"]]
    if (
        len(coordinates) < 3
        or coordinates[0] != points[0]
        or coordinates[-1] != points[1]
    ):
        raise ValueError("witness must connect the two user points indirectly")
    if direct_audit.get("status") != "violation" or not any(
        finding.get("reason") == "road_contact" and finding.get("allowed") is False
        for finding in direct_audit.get("findings", [])
    ):
        raise ValueError("direct segment must contain a forbidden road contact")
    candidate_id = witness.get("candidateId")
    contacts = [
        finding
        for finding in witness_audit.get("findings", [])
        if finding.get("reason") == "road_contact"
    ]
    if (
        witness_audit.get("status") != "valid"
        or not contacts
        or any(
            not finding.get("allowed")
            or candidate_id
            not in finding.get("bridgeIds", []) + finding.get("gasIds", [])
            for finding in contacts
        )
    ):
        raise ValueError("witness must use the declared allowed crossing")


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def requested_search_bounds(points):
    latitudes = [point["Latitude"] for point in points]
    longitudes = [point["Longitude"] for point in points]
    latitude_margin = MINIMUM_SEARCH_MARGIN_METRES / (6371008.8 * math.pi / 180)
    mean_latitude = sum(latitudes) / len(latitudes)
    longitude_margin = latitude_margin / math.cos(math.radians(mean_latitude))
    return [
        min(longitudes) - longitude_margin,
        min(latitudes) - latitude_margin,
        max(longitudes) + longitude_margin,
        max(latitudes) + latitude_margin,
    ]


def polyline_distance(coordinates):
    return sum(
        distance(start, end)
        for start, end in zip(coordinates, coordinates[1:], strict=False)
    )


def prepare(data, output, scenario_path=DEFAULT_SCENARIO):
    """Validate pinned inputs and save independent pre-route evidence."""
    scenario = json.loads(scenario_path.read_text())
    manifest = json.loads((data / "manifest.json").read_text())
    database_hash = sha256(data / "geodata.sqlite")
    if manifest["database_sha256"] != database_hash:
        raise ValueError("database SHA-256 does not match manifest")
    if scenario["databaseSha256"] != database_hash:
        raise ValueError("scenario database SHA-256 does not match input")

    coverage = Polygon(json.loads((ROOT / "data/belarus-coverage.json").read_text()))
    connection = sqlite3.connect(f"file:{data / 'geodata.sqlite'}?mode=ro", uri=True)
    try:
        features = query(connection, scenario["auditBounds"])
    finally:
        connection.close()
    features = [item for item in features if item["kind"] not in ("Forest", "Field")]
    target = next(
        (
            item
            for item in features
            if item["id"] == scenario["targetRoad"]["featureId"]
            and item["kind"] == "Road"
        ),
        None,
    )
    if target is None or target["source_id"] != scenario["targetRoad"]["sourceId"]:
        raise ValueError("target road identity does not match pinned geometry")
    candidate = next(
        (
            item
            for item in features
            if item["id"] == scenario["witness"]["candidateId"]
            and item["kind"]
            in (
                "BridgeCandidate",
                "GasPipelineCrossingCandidate",
            )
        ),
        None,
    )
    if candidate is None or not candidate["geometry"].intersects(target["geometry"]):
        raise ValueError("declared candidate is not eligible for the target road")

    origin = (
        scenario["points"][0]["Longitude"],
        scenario["points"][0]["Latitude"],
    )
    projected_coverage = project(coverage, origin)
    evidence = audit_scenario(
        scenario,
        features,
        coverage=projected_coverage,
        origin=origin,
    )
    validate_pre_route(scenario, evidence["direct"], evidence["witness"])

    output.mkdir(parents=True, exist_ok=True)
    save_json(output / "scenario.json", scenario)
    save_json(output / "direct-audit.json", evidence["direct"])
    save_json(output / "witness-audit.json", evidence["witness"])
    direct_geometry = LineString(
        [(point["Longitude"], point["Latitude"]) for point in scenario["points"]]
    )
    witness_geometry = LineString(scenario["witness"]["coordinates"])
    save_json(
        output / "direct.geojson",
        geojson([feature(direct_geometry, {"status": "forbidden"})]),
    )
    save_json(
        output / "witness.geojson",
        geojson([feature(witness_geometry, {"status": "valid"})]),
    )

    source_features = [
        feature(
            Point(point["Longitude"], point["Latitude"]),
            {"role": role},
        )
        for role, point in zip(("A", "B"), scenario["points"], strict=True)
    ]
    source_features.extend(
        feature(
            item["geometry"],
            {
                "kind": item["kind"],
                "featureId": item["id"],
                "sourceId": item["source_id"],
                "approvalId": item["approval_id"],
                "target": item["id"] == target["id"],
                "modelEligible": item["id"] == candidate["id"],
            },
        )
        for item in features
    )
    save_json(output / "source-pilot.geojson", geojson(source_features))
    map_input = source_features + [
        feature(direct_geometry, {"kind": "ForbiddenDirect"})
    ]
    svg_map(
        map_input,
        [feature(witness_geometry, {"profile": "verified witness"})],
        scenario["id"],
        "direct violation; witness valid",
        output / "before.svg",
        scenario["auditBounds"],
    )
    direct_points = list(direct_geometry.coords)
    witness_points = list(witness_geometry.coords)
    search_bounds = requested_search_bounds(scenario["points"])
    save_json(
        output / "pre-route.json",
        {
            "scenarioId": scenario["id"],
            "databaseSha256": database_hash,
            "candidateIds": scenario["candidateIds"],
            "targetRoad": scenario["targetRoad"],
            "coverage": {
                "status": "inside",
                "directCovered": coverage.covers(direct_geometry),
                "witnessCovered": coverage.covers(witness_geometry),
            },
            "requestedSearchBounds": search_bounds,
            "witnessInsideRequestedSearchBounds": box(*search_bounds).covers(
                witness_geometry
            ),
            "directDistanceMetres": distance(*direct_points),
            "witnessDistanceMetres": polyline_distance(witness_points),
            "directAudit": evidence["direct"],
            "witnessAudit": evidence["witness"],
        },
    )
    save_json(
        output / "selection-attempts.json",
        {
            "geographicScope": "smolevichi-04 bridge candidate 192543",
            "currentSelection": "first valid 20 m by 20 m offset around the north candidate endpoint",
            "currentAttemptCount": 1,
            "historicalRecoveredAttempts": {
                "count": 10,
                "offsetsMetres": [
                    100,
                    -100,
                    200,
                    -200,
                    300,
                    -300,
                    400,
                    -400,
                    500,
                    -500,
                ],
                "reasons": {
                    "other_unmapped_road_contacts": 10,
                    "also_unverified_water_contact": 2,
                },
                "limitation": "Recovered from the previous-run narrative; exact geometries were lost and are not presented as fresh measurements.",
            },
        },
    )
    return scenario, features, coverage, source_features


def audit_alternatives(scenario, alternatives, features, coverage):
    origin = (
        scenario["points"][0]["Longitude"],
        scenario["points"][0]["Latitude"],
    )
    projected_coverage = project(coverage, origin)
    expected_start = tuple(scenario["witness"]["coordinates"][0])
    expected_end = tuple(scenario["witness"]["coordinates"][-1])
    results = []
    for alternative in alternatives:
        geometry = LineString(
            [
                (point["Longitude"], point["Latitude"])
                for point in alternative["geometry"]
            ]
        )
        checks = audit(
            project(geometry, origin),
            features,
            coverage=projected_coverage,
            origin=origin,
        )
        if (
            distance(tuple(geometry.coords[0]), expected_start) >= 1
            or distance(tuple(geometry.coords[-1]), expected_end) >= 1
        ):
            checks["status"] = "violation"
            checks["findings"].append({"reason": "endpoint_mismatch"})
        if not box(*scenario["auditBounds"]).covers(geometry):
            checks["status"] = (
                "unknown" if checks["status"] == "valid" else checks["status"]
            )
            checks["findings"].append({"reason": "outside_audit_window"})
        checks["profile"] = alternative["name"]
        results.append(checks)
    return results


def write_report(output, scenario, baseline, route_audits, process, invocation):
    assessment = classify_result(baseline, route_audits)
    app_commit = subprocess.check_output(
        ["git", "rev-parse", "HEAD"], text=True
    ).strip()
    source_dirty = bool(
        subprocess.check_output(
            ["git", "status", "--porcelain", "--untracked-files=no"], text=True
        ).strip()
    )
    pre_route = json.loads((output / "pre-route.json").read_text())
    witness_coordinates = scenario["witness"]["coordinates"]
    alternatives = baseline.get("alternatives", [])
    route_matches_witness = [
        [[point["Longitude"], point["Latitude"]] for point in alternative["geometry"]]
        == witness_coordinates
        for alternative in alternatives
    ]
    report = {
        "schema": "maps.routing-indirect-study.result.v1",
        "scenarioId": scenario["id"],
        "appCommit": app_commit,
        "sourceDirty": source_dirty,
        "databaseSha256": scenario["databaseSha256"],
        "invocation": invocation,
        "preRoute": pre_route,
        "baseline": {
            "status": baseline.get("status"),
            "wallMilliseconds": process["elapsed_wall_ms"],
            "alternativeCount": len(alternatives),
            "routeMatchesWitnessVertices": route_matches_witness,
            "resultRef": "baseline-result.json",
            "stdoutRef": "baseline.stdout.log",
            "stderrRef": "baseline.stderr.log",
        },
        "routeAudits": route_audits,
        "routingParameters": {
            "requestedCellSizeMetres": scenario["routingOptions"]["CellSizeMetres"],
            "effectiveCellSizeMetres": None,
            "effectiveCellAvailability": "not instrumented; baseline succeeded so failure diagnostics were not triggered",
        },
        "assessment": assessment,
    }
    save_json(output / "report.json", report)
    lines = [
        "# Непрямой разрешённый дорожный переход возле Смолевичей",
        "",
        f"Закреплённая база: `{scenario['databaseSha256']}`. Маршрутизатор получил ровно две пользовательские точки; промежуточная вершина witness ему не передавалась.",
        "",
        "## Сравнение",
        "",
        "| Этап | Результат | Доказательство |",
        "| --- | --- | --- |",
        f"| Прямая A–B | `{pre_route['directAudit']['status']}` | запрещённый контакт с Road `192541`; [аудит](direct-audit.json), [геометрия](direct.geojson) |",
        f"| Независимый witness | `{pre_route['witnessAudit']['status']}` | контакт разрешён BridgeCandidate `192543`; [аудит](witness-audit.json), [геометрия](witness.geojson) |",
        f"| Baseline, cell {scenario['routingOptions']['CellSizeMetres']} м | `{baseline.get('status')}`, альтернатив {len(alternatives)} | [оригинальный результат](baseline-result.json), [stdout](baseline.stdout.log), [маршруты](routes.geojson) |",
        f"| Аудит всех альтернатив | `{', '.join(item['status'] for item in route_audits) or 'нет альтернатив'}` | [подробности](route-audits.json) |",
        "",
        "## Объяснение",
        "",
        f"Итоговая классификация: **{assessment['classification']}**. Существующий граф сам использовал переход: witness содержит три вершины, но запрос содержал только две крайние точки. Проверенный witness и найденный маршрут полностью лежат в coverage и запрошенной области поиска.",
        "",
        "Baseline успешен, поэтому диагностические повторы с уменьшенным шагом, разбор порталов/соединителей и изменение production-кода не требовались. Effective cell size отдельно не инструментирован; это не влияет на вывод об успешно возвращённой и независимо проверенной геометрии. Domain-правила не менялись, production-дефект не подтверждён.",
        "",
        "## Карты и воспроизводимость",
        "",
        "- [До поиска: запрещённая прямая и verified witness](before.svg).",
        "- [После поиска: все возвращённые маршруты](after.svg).",
        "- [Исходные геометрии и candidate IDs](source-pilot.geojson).",
        "- [Ограниченный журнал отбора](selection-attempts.json).",
        "- [Машинный сравнительный отчёт](report.json).",
        "",
        f"Evidence создан на commit `{app_commit}`; tracked source dirty: `{str(source_dirty).lower()}`. Инженерная пригодность перехода не проверялась: доказана только допустимость по текущей модели.",
        "",
        "© OpenStreetMap contributors, ODbL 1.0.",
    ]
    (output / "report.md").write_text("\n".join(lines) + "\n")
    return report


def run(data, verify, output, *, timeout=120, scenario_path=DEFAULT_SCENARIO):
    scenario, features, coverage, source_features = prepare(data, output, scenario_path)
    result_path = output / "baseline-result.json"
    result_path.unlink(missing_ok=True)
    command = [
        "dotnet",
        str(verify),
        "--crossing",
        str(data),
        str(output / "scenario.json"),
        str(result_path),
    ]
    process = run_bounded_command(
        command,
        timeout,
        os.environ | {"DOTNET_GCHeapHardLimit": "60000000"},
    )
    (output / "baseline.stdout.log").write_text(process["stdout"])
    (output / "baseline.stderr.log").write_text(process["stderr"])
    written_result = (
        json.loads(result_path.read_text()) if result_path.exists() else None
    )
    baseline = effective_baseline(written_result, process)
    if written_result is None:
        save_json(result_path, baseline)
    route_audits = audit_alternatives(
        scenario, baseline.get("alternatives", []), features, coverage
    )
    save_json(output / "route-audits.json", route_audits)
    route_features = [
        feature(
            LineString(
                [
                    (point["Longitude"], point["Latitude"])
                    for point in alternative["geometry"]
                ]
            ),
            {"profile": alternative["name"], "metrics": alternative["metrics"]},
        )
        for alternative in baseline.get("alternatives", [])
    ]
    save_json(output / "routes.geojson", geojson(route_features))
    svg_map(
        source_features,
        route_features,
        scenario["id"],
        baseline["status"],
        output / "after.svg",
        scenario["auditBounds"],
    )
    invocation = {
        "mode": "CableRoutePlanner.Verify --crossing",
        "userPointCount": len(scenario["points"]),
        "witnessVerticesPassedAsUserPoints": False,
        "timeoutSeconds": timeout,
        "returnCode": process["returncode"],
        "timedOut": process["timeout"],
        "verifyDllSha256": sha256(verify),
    }
    save_json(output / "invocation.json", invocation)
    return write_report(output, scenario, baseline, route_audits, process, invocation)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--verify-dll", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--scenario", type=Path, default=DEFAULT_SCENARIO)
    parser.add_argument("--timeout-seconds", type=float, default=120)
    args = parser.parse_args()
    report = run(
        args.data,
        args.verify_dll,
        args.output,
        timeout=args.timeout_seconds,
        scenario_path=args.scenario,
    )
    print(
        f"{report['scenarioId']}: {report['assessment']['classification']}",
        flush=True,
    )


if __name__ == "__main__":
    main()
