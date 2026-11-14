"""Bounded underpass evidence using the existing Verify CLI and independent audit."""

import argparse
import hashlib
import json
import os
import sqlite3
import subprocess
from collections import Counter
from pathlib import Path

from benchmark_routes import run_bounded_command
from routing_crossing_pilot import (
    OPTIONS,
    audit,
    distance,
    effective_result,
    feature,
    geojson,
    project,
    query,
    save_json,
    svg_map,
    unproject,
)
from shapely import from_wkb
from shapely.geometry import LineString, Polygon

DATABASE_SHA256 = "f584e0c9963692b31b0f2b6322dc810641729bbabcc410626faf8ea70b703493"
CORPUS = Path("samples/routing-crossing-mass-v1.json")


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def execute(
    data, verify, folder, scenario, enabled, connection, coverage, *, existing=False
):
    folder.mkdir(parents=True, exist_ok=True)
    scenario = scenario | {
        "databaseSha256": DATABASE_SHA256,
        "routingOptions": scenario["routingOptions"]
        | {"AllowExperimentalPedestrianUnderpasses": enabled},
    }
    if len(scenario["points"]) != 2:
        raise ValueError("Exactly two user points required")
    result_path = folder / "result.json"
    if existing:
        saved = json.loads((folder / "scenario.json").read_text())
        if any(
            saved[key] != scenario[key]
            for key in ("points", "routingOptions", "databaseSha256")
        ):
            raise ValueError("Saved route parameters differ from the frozen scenario")
        result = json.loads(result_path.read_text())
    else:
        save_json(folder / "scenario.json", scenario)
        result_path.unlink(missing_ok=True)
        process = run_bounded_command(
            [
                "dotnet",
                str(verify),
                "--crossing",
                str(data),
                str(folder / "scenario.json"),
                str(result_path),
            ],
            120,
            os.environ | {"DOTNET_GCHeapHardLimit": "60000000"},
        )
        (folder / "stdout.log").write_text(process["stdout"])
        (folder / "stderr.log").write_text(process["stderr"])
        result = effective_result(
            json.loads(result_path.read_text()) if result_path.exists() else None,
            process,
        )
        result["wallMilliseconds"] = process["elapsed_wall_ms"]
    route_features = []
    audits = []
    for alternative in result["alternatives"]:
        route = LineString(
            [(p["Longitude"], p["Latitude"]) for p in alternative["geometry"]]
        )
        origin = (scenario["points"][0]["Longitude"], scenario["points"][0]["Latitude"])
        # Query the ACTUAL route envelope, not a preselected witness window.
        w, s, e, n = route.bounds
        features = query(connection, (w - 0.001, s - 0.001, e + 0.001, n + 0.001))
        checks = audit(
            project(route, origin),
            features,
            project(coverage, origin),
            origin,
            allow_underpasses=enabled,
        )
        for actual, expected in zip(
            (route.coords[0], route.coords[-1]), scenario["points"], strict=True
        ):
            if distance(actual, (expected["Longitude"], expected["Latitude"])) >= 1:
                checks["status"] = "violation"
                checks["findings"].append({"reason": "endpoint_mismatch"})
        audits.append(checks)
        route_features.append(
            feature(
                route, {"name": alternative["name"], "metrics": alternative["metrics"]}
            )
        )
    result["audits"] = audits
    save_json(result_path, result)
    save_json(folder / "routes.geojson", geojson(route_features))
    print(
        scenario["id"],
        enabled,
        result["status"],
        [a["status"] for a in audits],
        flush=True,
    )
    return result


def summary(result):
    return {
        "status": result["status"],
        "error": result.get("error"),
        "wallMilliseconds": result["wallMilliseconds"],
        "alternatives": [
            {"name": a["name"], "metrics": a["metrics"], "audit": audit_result}
            for a, audit_result in zip(
                result["alternatives"], result["audits"], strict=True
            )
        ],
    }


def confirmed(off, on, candidate_id):
    return (
        off["status"] == "no_route"
        and not off["alternatives"]
        and on["status"] == "success"
        and bool(on["alternatives"])
        and len(on["audits"]) == len(on["alternatives"])
        and all(a["status"] == "valid" for a in on["audits"])
        and any(
            candidate_id in f.get("underpassIds", [])
            for a in on["audits"]
            for f in a["findings"]
        )
    )


def study(args):
    args.output.mkdir(parents=True, exist_ok=True)
    if digest(args.data / "geodata.sqlite") != DATABASE_SHA256:
        raise ValueError("Expected the already-built SQLite v3 Belarus database")
    manifest = json.loads((args.data / "manifest.json").read_text())
    if manifest["schema_version"] != 3:
        raise ValueError("SQLite v3 required")
    coverage = Polygon(json.loads(Path("data/belarus-coverage.json").read_text()))
    report = dict(
        databaseSha256=DATABASE_SHA256,
        schemaVersion=3,
        appCommit=subprocess.check_output(
            ["git", "rev-parse", "HEAD"], text=True
        ).strip(),
        verifyDllSha256=digest(args.verify_dll),
        corpusSha256=digest(CORPUS),
        workflowRunId=os.environ.get("GITHUB_RUN_ID"),
        userPointCount=2,
        intermediateWaypoints=False,
        attempts=[],
        confirmed=None,
    )
    with sqlite3.connect(
        f"file:{args.data / 'geodata.sqlite'}?mode=ro", uri=True
    ) as connection:
        candidates = connection.execute(
            "SELECT id,source_id,wkb FROM features WHERE kind='PedestrianUnderpassCandidate' "
            "ORDER BY CASE WHEN source_id='way/24422904' THEN 0 ELSE 1 END,id"
        ).fetchall()
        report["candidateCount"] = len(candidates)
        if len(candidates) != 444 or candidates[0][1] != "way/24422904":
            raise ValueError("Unexpected candidate inventory")
        for candidate_id, source, raw in candidates[:12]:
            span = from_wkb(raw)
            origin = span.coords[0]
            projected = project(span, origin)
            coords = list(projected.coords)
            # Deterministic pairs near the two entrances; none are intermediate waypoints.
            for extension in (10, 30, 0):
                start, end = coords[0], coords[-1]

                def extend(a, b, extension=extension):
                    length = ((a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2) ** 0.5
                    return (
                        a[0] + extension * (a[0] - b[0]) / length,
                        a[1] + extension * (a[1] - b[1]) / length,
                    )

                points = [
                    unproject(*extend(start, coords[1]), origin),
                    unproject(*extend(end, coords[-2]), origin),
                ]
                scenario = dict(
                    id=f"{source.replace('/', '-')}-{extension}",
                    points=[dict(Longitude=x, Latitude=y) for x, y in points],
                    routingOptions=OPTIONS,
                )
                folder = args.output / scenario["id"]
                off = execute(
                    args.data,
                    args.verify_dll,
                    folder / "false",
                    scenario,
                    False,
                    connection,
                    coverage,
                )
                on = execute(
                    args.data,
                    args.verify_dll,
                    folder / "true",
                    scenario,
                    True,
                    connection,
                    coverage,
                )
                record = dict(
                    candidateId=candidate_id,
                    sourceId=source,
                    scenario=scenario,
                    off=summary(off),
                    on=summary(on),
                )
                report["attempts"].append(record)
                if confirmed(off, on, candidate_id):
                    report["confirmed"] = record
                    routes = json.loads((folder / "true/routes.geojson").read_text())
                    save_json(args.output / "route.geojson", routes)
                    bounds = span.buffer(0.002).bounds
                    context = [
                        feature(
                            f["geometry"], {"kind": f["kind"], "featureId": f["id"]}
                        )
                        for f in query(connection, bounds)
                    ]
                    save_json(args.output / "context.geojson", geojson(context))
                    svg_map(
                        context,
                        routes["features"],
                        source,
                        "independent audit: valid",
                        args.output / "route.svg",
                        bounds,
                    )
                save_json(args.output / "results.json", report)
                if report["confirmed"]:
                    break
            if report["confirmed"]:
                break
        if not report["confirmed"]:
            raise RuntimeError(
                "Bounded search found no independently valid flag-changing case"
            )
        mass = []
        for scenario in json.loads(CORPUS.read_text()):
            folder = args.output / "mass" / scenario["id"]
            off = execute(
                args.data,
                args.verify_dll,
                folder / "false",
                scenario,
                False,
                connection,
                coverage,
            )
            on = execute(
                args.data,
                args.verify_dll,
                folder / "true",
                scenario,
                True,
                connection,
                coverage,
            )
            mass.append(dict(id=scenario["id"], off=summary(off), on=summary(on)))
            report["mass"] = mass
            save_json(args.output / "results.json", report)
        report["massCounts"] = {
            flag: dict(Counter(r[flag]["status"] for r in mass))
            for flag in ("off", "on")
        }
        report["historicalV2"] = {
            key: json.loads(
                Path("docs/evidence/routing-mass-v1/results.json").read_text()
            )[key]
            for key in ("baselineAppCommit", "databaseSha256", "metrics")
        }
        save_json(args.output / "results.json", report)
        if len(mass) != 88 or any(
            r[flag]["status"] not in ("success", "no_route")
            or any(a["audit"]["status"] != "valid" for a in r[flag]["alternatives"])
            for r in mass
            for flag in ("off", "on")
        ):
            raise RuntimeError("Incomplete mass comparison or invalid route audit")


def reaudit(args):
    """Recheck saved Verify geometries against the same SQLite without rerouting."""
    if digest(args.data / "geodata.sqlite") != DATABASE_SHA256:
        raise ValueError("Expected the already-built SQLite v3 Belarus database")
    report = json.loads((args.output / "results.json").read_text())
    if report["databaseSha256"] != DATABASE_SHA256 or report["corpusSha256"] != digest(
        CORPUS
    ):
        raise ValueError("Saved experiment provenance differs")
    coverage = Polygon(json.loads(Path("data/belarus-coverage.json").read_text()))
    frozen = {s["id"]: s for s in json.loads(CORPUS.read_text())}
    if set(frozen) != {s["id"] for s in report["mass"]} or len(report["mass"]) != 88:
        raise ValueError("Expected exactly the frozen 88 cases")
    with sqlite3.connect(
        f"file:{args.data / 'geodata.sqlite'}?mode=ro", uri=True
    ) as connection:
        records = [
            (a, args.output / a["scenario"]["id"], a["scenario"])
            for a in report["attempts"]
        ]
        records += [
            (a, args.output / "mass" / a["id"], frozen[a["id"]]) for a in report["mass"]
        ]
        for record, folder, scenario in records:
            off = execute(
                args.data,
                args.verify_dll,
                folder / "false",
                scenario,
                False,
                connection,
                coverage,
                existing=True,
            )
            on = execute(
                args.data,
                args.verify_dll,
                folder / "true",
                scenario,
                True,
                connection,
                coverage,
                existing=True,
            )
            record.update(off=summary(off), on=summary(on))
            if (
                record.get("candidateId") == report["confirmed"]["candidateId"]
                and scenario["id"] == report["confirmed"]["scenario"]["id"]
            ):
                if not confirmed(off, on, record["candidateId"]):
                    raise RuntimeError("Saved real case failed the strengthened audit")
                report["confirmed"] = record
                actual = LineString(
                    [
                        (p["Longitude"], p["Latitude"])
                        for p in on["alternatives"][0]["geometry"]
                    ]
                )
                w, s, e, n = actual.bounds
                features = query(
                    connection, (w - 0.001, s - 0.001, e + 0.001, n + 0.001)
                )
                origin = actual.coords[0]
                report["confirmedRouteWithFlagOffAudit"] = audit(
                    project(actual, origin), features, project(coverage, origin), origin
                )
                used = sorted(
                    {
                        cid
                        for check in on["audits"]
                        for finding in check["findings"]
                        for cid in finding.get("underpassIds", [])
                    }
                )
                placeholders = ",".join("?" for _ in used)
                report["confirmedSqliteLinks"] = [
                    dict(
                        candidateId=row[0],
                        roadId=row[1],
                        tunnelSourceId=row[2],
                        roadSourceId=row[3],
                    )
                    for row in connection.execute(
                        f"SELECT candidate_id,road_id,tunnel_osm_id,road_osm_id FROM underpass_roads WHERE candidate_id IN ({placeholders}) ORDER BY candidate_id,road_id",
                        used,
                    )
                ]
    report["auditCommit"] = subprocess.check_output(
        ["git", "rev-parse", "HEAD"], text=True
    ).strip()
    report["auditWorkflowRunId"] = os.environ.get("GITHUB_RUN_ID")
    save_json(args.output / "results.json", report)
    if report["confirmedRouteWithFlagOffAudit"]["status"] != "violation" or any(
        item[flag]["status"] not in ("success", "no_route")
        or any(a["audit"]["status"] != "valid" for a in item[flag]["alternatives"])
        for item in report["mass"]
        for flag in ("off", "on")
    ):
        raise RuntimeError("Saved comparison failed the strengthened audit")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--verify-dll", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--existing", action="store_true")
    args = parser.parse_args()
    (reaudit if args.existing else study)(args)
