"""Audit seven known source witnesses and the actual head results independently."""

import argparse
import gzip
import hashlib
import json
import math
import sqlite3
import zlib
from contextlib import closing
from pathlib import Path

from routing_regression import (
    audit_alternative,
    complete_diagnostics,
    complete_no_route_error,
    data_provenance,
    digest,
    evidence_snapshot,
    save_json,
    search_meshes,
    validate_snapshot,
)
from shapely.errors import GEOSException
from shapely.geometry import Polygon

ROOT = Path(__file__).resolve().parents[1]
CATALOGUE = Path("docs/evidence/routing-recovery/witnesses.json")
KNOWN_IDS = frozenset(
    (
        "mass-naliboki-bridge-02",
        "mass-gomel-bridge-01",
        "mass-gomel-bridge-03",
        "mass-gomel-bridge-05",
        "mass-naliboki-multi-06",
        "mass-smolevichi-no_candidate-11",
        "mass-pukhovichi-multi-07",
    )
)


def canonical_digest(value):
    return hashlib.sha256(
        json.dumps(
            value, sort_keys=True, separators=(",", ":"), ensure_ascii=False
        ).encode()
    ).hexdigest()


def load_catalogue(root, corpus, catalogue=None):
    """Bind exact witness geometry to frozen inputs and lossless source fixtures."""
    catalogue = root / CATALOGUE if catalogue is None else catalogue
    result = json.loads(catalogue.read_text(encoding="utf-8"))
    records = result["records"]
    if (
        result.get("schema") != "maps.routing-recovery-witnesses.v1"
        or len(records) != len(KNOWN_IDS)
        or {record["id"] for record in records} != KNOWN_IDS
        or result.get("corpusSha256") != digest(corpus)
    ):
        raise ValueError(
            "Known-witness catalogue inventory or corpus provenance differs"
        )
    scenarios = {
        scenario["id"]: scenario
        for scenario in json.loads(corpus.read_text(encoding="utf-8"))
    }
    for record in records:
        relative = Path(record["fixture"])
        path = (root / relative).resolve()
        if (
            relative.is_absolute()
            or not path.is_relative_to(root.resolve())
            or digest(path) != record["fixtureSha256"]
        ):
            raise ValueError("Known-witness source fixture checksum differs")
        raw = gzip.decompress(path.read_bytes())
        fixture = json.loads(raw)
        scenario = scenarios[record["id"]]
        if (
            hashlib.sha256(raw).hexdigest() != record["fixtureJsonSha256"]
            or canonical_digest(scenario) != record["originalScenarioSha256"]
            or fixture["originalScenario"] != scenario
            or fixture["independentWitness"]["alternative"]["geometry"]
            != record["geometry"]
        ):
            raise ValueError("Known-witness geometry or original scenario differs")
        geometry = record["geometry"]
        if (
            len(geometry) < 2
            or geometry[0] != scenario["points"][0]
            or geometry[-1] != scenario["points"][-1]
            or any(
                type(point.get(key)) not in (int, float)
                or not math.isfinite(point[key])
                for point in geometry
                for key in ("Longitude", "Latitude")
            )
        ):
            raise ValueError("Known-witness coordinates or endpoints are invalid")
        if (
            "provenance" in fixture
            and fixture["provenance"]["sourceSha256"] != result["sourceSha256"]
        ):
            raise ValueError("Known-witness PBF provenance differs")
        if "features" in fixture and not set(record["stableSourceIds"]).issubset(
            {feature["sourceId"] for feature in fixture["features"]}
        ):
            raise ValueError("Known-witness stable source inventory differs")
    return result


def _raw_head(record, scenario, run_report, default_underpasses):
    """Retain the runner's process, invocation, diagnostics and raw route gates."""
    process = record.get("process", {})
    if (
        process.get("timeout") is not False
        or type(process.get("returncode")) is not int
        or process["returncode"] != 0
    ):
        raise ValueError("Head process failed or timed out")
    path = (run_report.parent / record["rawResult"]).resolve()
    if not path.is_relative_to(run_report.parent.resolve()):
        raise ValueError("Head raw result escapes evidence directory")
    original, invocation = (
        path.parent / "original-scenario.json",
        path.parent / "scenario.json",
    )
    if (
        digest(original) != record.get("originalScenarioSha256")
        or digest(invocation) != record.get("invocationSha256")
        or json.loads(original.read_text(encoding="utf-8")) != scenario
    ):
        raise ValueError("Head original input provenance differs")
    actual = json.loads(invocation.read_text(encoding="utf-8"))
    if actual != scenario | {"includeDiagnostics": True}:
        raise ValueError("Head invocation changed original routing inputs")
    enabled = actual["routingOptions"].get(
        "AllowExperimentalPedestrianUnderpasses", default_underpasses
    )
    if type(enabled) is not bool or record.get("effectiveUnderpasses") is not enabled:
        raise ValueError("Head effective underpass flag differs")
    raw = json.loads(path.read_text(encoding="utf-8"))
    status = raw.get("status")
    alternatives = raw.get("alternatives")
    error = raw.get("error") or {}
    if (
        status not in ("success", "no_route")
        or status != record.get("status")
        or not isinstance(alternatives, list)
        or any(not isinstance(alternative, dict) for alternative in alternatives)
        or (status == "success") != bool(alternatives)
        or (status == "no_route" and not complete_no_route_error(error))
        or not complete_diagnostics(raw.get("diagnostics"), status, error.get("reason"))
        or raw.get("diagnostics") != record.get("diagnostics")
    ):
        raise ValueError("Head raw result or diagnostics are incomplete")
    if status == "success" and (
        record.get("gatePassed") is not True or record.get("auditStatus") != "valid"
    ):
        raise ValueError("Head route gate did not pass")
    return raw, enabled


def investigate(data, corpus, catalogue, coverage_path, run_report, *, root=ROOT):
    report = {
        "schema": "maps.routing-recovery-audit.v1",
        "gatePassed": False,
        "records": [],
        "failures": [],
        "missedWitnessIds": [],
    }
    try:
        known = load_catalogue(root, corpus, catalogue)
        provenance = data_provenance(data)
        head = json.loads(run_report.read_text(encoding="utf-8"))
        if (
            not isinstance(head, dict)
            or not isinstance(head.get("provenance"), dict)
            or head.get("provenanceRevalidated", {}).get("unchanged") is not True
            or "fatalError" in head
        ):
            raise ValueError(
                "Head provenance snapshot was not successfully revalidated"
            )
        expected = provenance | {
            "corpusSha256": digest(corpus),
            "knownWitnessCatalogueSha256": digest(catalogue),
        }
        report["provenance"] = expected
        mismatches = [
            key
            for key in (
                "databaseSha256",
                "sourceSha256",
                "corpusSha256",
                "knownWitnessCatalogueSha256",
            )
            if head.get("provenance", {}).get(key) != expected[key]
        ]
        if mismatches or known["sourceSha256"] != provenance["sourceSha256"]:
            report["failures"].append(
                {"reason": "provenance_mismatch", "fields": mismatches}
            )
            return report
        results = [
            record
            for record in head.get("results", [])
            if isinstance(record, dict)
            and record.get("group") == "frozen"
            and record.get("id") in KNOWN_IDS
        ]
        if (
            len(results) != len(KNOWN_IDS)
            or {record["id"] for record in results} != KNOWN_IDS
        ):
            raise ValueError("Known head-result inventory differs")
        head_by_id = {record["id"]: record for record in results}
        scenarios = {
            scenario["id"]: scenario
            for scenario in json.loads(corpus.read_text(encoding="utf-8"))
        }
        coverage = Polygon(json.loads(coverage_path.read_text(encoding="utf-8")))
        paths = {
            "database": data / "geodata.sqlite",
            "manifest": data / "manifest.json",
            "corpus": corpus,
            "catalogue": catalogue,
            "coverage": coverage_path,
            "headReport": run_report,
            "auditor": Path(__file__),
            "routingAuditor": Path(__file__).with_name("routing_regression.py"),
            "geometryAuditor": Path(__file__).with_name("routing_crossing_pilot.py"),
        }
        if (data / "package.json").exists():
            paths["packageReceipt"] = data / "package.json"
        for witness in known["records"]:
            paths[f"fixture/{witness['id']}"] = root / witness["fixture"]
            path = (
                run_report.parent / head_by_id[witness["id"]]["rawResult"]
            ).resolve()
            for name in ("result.json", "original-scenario.json", "scenario.json"):
                paths[f"head/{witness['id']}/{name}"] = path.parent / name
        before = evidence_snapshot(paths)
        report["provenance"]["evidenceHashes"] = before
        with closing(
            sqlite3.connect(
                (data / "geodata.sqlite").resolve().as_uri() + "?mode=ro", uri=True
            )
        ) as connection:
            for witness in known["records"]:
                case_id = witness["id"]
                checked = {"id": case_id}
                report["records"].append(checked)
                try:
                    enabled = head_by_id[case_id].get("effectiveUnderpasses")
                    if type(enabled) is not bool:
                        raise ValueError("Head effective underpass flag is not boolean")
                    checked["effectiveUnderpasses"] = enabled
                    checked["witnessAudit"] = audit_alternative(
                        connection,
                        coverage,
                        scenarios[case_id],
                        {"geometry": witness["geometry"]},
                        enabled=enabled,
                    )
                    raw, enabled = _raw_head(
                        head_by_id[case_id],
                        scenarios[case_id],
                        run_report,
                        head["provenance"]["effectiveDefaultUnderpasses"],
                    )
                    if checked["witnessAudit"]["status"] != "valid":
                        checked["reason"] = "witness_not_valid"
                    elif raw["status"] == "no_route":
                        checked["reason"] = "missed_valid_path"
                        report["missedWitnessIds"].append(case_id)
                    else:
                        checked["headAudits"] = [
                            audit_alternative(
                                connection,
                                coverage,
                                scenarios[case_id],
                                alternative,
                                enabled=enabled,
                            )
                            for alternative in raw["alternatives"]
                        ]
                        meshes = search_meshes(raw["diagnostics"])
                        bounds = [
                            meshes[-1][key]
                            for key in ("West", "South", "East", "North")
                        ]
                        checked["headSearchBounds"] = bounds
                        west, south, east, north = bounds
                        within = all(
                            west <= point["Longitude"] <= east
                            and south <= point["Latitude"] <= north
                            for alternative in raw["alternatives"]
                            for point in alternative["geometry"]
                        )
                        checked["reason"] = (
                            "valid_route_found"
                            if within
                            and all(
                                audit["status"] == "valid"
                                for audit in checked["headAudits"]
                            )
                            else "head_route_not_valid"
                        )
                    if checked["reason"] != "valid_route_found":
                        report["failures"].append(
                            {"id": case_id, "reason": checked["reason"]}
                        )
                except (
                    OSError,
                    ValueError,
                    KeyError,
                    TypeError,
                    sqlite3.Error,
                    GEOSException,
                    IndexError,
                    AttributeError,
                ) as error:
                    checked["reason"] = "technical_failure"
                    report["failures"].append(
                        {
                            "id": case_id,
                            "reason": "technical_failure",
                            "errorType": type(error).__name__,
                            "detail": str(error),
                        }
                    )
        validate_snapshot(paths, before)
        report["provenanceRevalidated"] = {
            "unchanged": True,
            "checkedFiles": len(paths),
        }
        report["gatePassed"] = not report["failures"]
    except (
        OSError,
        ValueError,
        KeyError,
        TypeError,
        sqlite3.Error,
        EOFError,
        IndexError,
        AttributeError,
        GEOSException,
        zlib.error,
    ) as error:
        report["failures"].append(
            {
                "reason": "provenance_or_input_failure",
                "errorType": type(error).__name__,
                "detail": str(error),
            }
        )
    return report


def main(argv=None, *, root=ROOT):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--run-report", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument(
        "--corpus", type=Path, default=root / "samples/routing-crossing-mass-v1.json"
    )
    parser.add_argument("--catalogue", type=Path, default=root / CATALOGUE)
    parser.add_argument(
        "--coverage", type=Path, default=root / "data/belarus-coverage.json"
    )
    args = parser.parse_args(argv)
    report = investigate(
        args.data,
        args.corpus,
        args.catalogue,
        args.coverage,
        args.run_report,
        root=root,
    )
    args.output.mkdir(parents=True, exist_ok=True)
    save_json(args.output / "results.json", report)
    lines = [
        "# Independent known-witness audit",
        "",
        f"Gate: {'PASS' if report['gatePassed'] else 'FAIL'}",
        "",
        "| Case | Result |",
        "| --- | --- |",
    ]
    lines.extend(
        f"| {record['id']} | {record['reason']} |" for record in report["records"]
    )
    lines.extend(
        [
            "",
            f"Confirmed misses: {len(report['missedWitnessIds'])}",
            f"Failures: {len(report['failures'])}",
        ]
    )
    (args.output / "summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("\n".join(lines))
    return 0 if report["gatePassed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
