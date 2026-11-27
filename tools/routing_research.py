"""Execute the frozen Belarus research corpus using existing regression auditors."""

import argparse
import datetime
import hashlib
import json
import math
import os
import platform
import shutil
import sqlite3
import subprocess
import sys
import time
from collections import Counter
from contextlib import closing
from pathlib import Path
from types import SimpleNamespace

from benchmark_routes import run_bounded_command
from routing_recovery import CATALOGUE, load_catalogue
from routing_regression import (
    CORPUS,
    CORPUS_SHA256,
    ROOT,
    audit_alternative,
    code_provenance,
    data_provenance,
    digest,
    evidence_snapshot,
    load_cases,
    run_cases,
    run_regression,
    save_json,
    validate_snapshot,
)
from routing_research_analysis import (
    aggregate,
    causes,
    classify,
    compare,
    fallback_metrics,
    markdown,
)
from shapely.geometry import Polygon

CORPUS_PATH = ROOT / "samples/routing-research-v1.json"
GENERATION_RECEIPT = ROOT / "docs/evidence/routing-research-v1/generation.json"
MANAGED_HEAP_BYTES = 1610612736
LEGACY_REGIONS = {
    "naliboki": "BY-MI",
    "pukhovichi": "BY-MI",
    "smolevichi": "BY-MI",
    "brest": "BY-BR",
    "gomel": "BY-HO",
    "vitebsk": "BY-VI",
}


def validate_budget(workers, timeout, probe_timeout):
    if type(workers) is not int or not 1 <= workers <= 8:
        raise ValueError("workers must be an integer in 1..8")
    if any(
        not math.isfinite(value) or value <= 0 for value in (timeout, probe_timeout)
    ):
        raise ValueError("process and research timeouts must be finite and positive")


def validate_build_location(verify, root=ROOT):
    expected = (
        root
        / "tools/CableRoutePlanner.Verify/bin/Release/net8.0/CableRoutePlanner.Verify.dll"
    )
    if verify.resolve() != expected.resolve() or not verify.is_file():
        raise ValueError(
            "Build Release Verify in this checkout; a foreign binary cannot inherit its source provenance"
        )


def verify_inventory(expected, records):
    ids = [record["id"] for record in records]
    if len(ids) != len(set(ids)) or set(ids) != set(expected):
        raise ValueError("Result inventory is incomplete, duplicated or unexpected")


def experiment_gates(frozen_passed, records):
    """A first research run may discover defects; retained regression must still pass."""
    return {
        "gatePassed": frozen_passed
        and not any(
            record["outcome"] == "technical_error"
            or (record.get("independentResearch") or {}).get("status")
            == "technical_error"
            for record in records
        ),
        "qualityGatePassed": not any(
            record["outcome"] in ("invalid_route", "confirmed_missed_route")
            for record in records
        ),
    }


def selected_cases(cases, evaluate_holdout):
    return [case for case in cases if evaluate_holdout or case["split"] != "holdout"]


def validate_generation_receipt(manifest, corpus_hash, receipt, coverage_hash):
    expected = {
        key: manifest[key]
        for key in (
            "sourceSha256",
            "legacyCorpusSha256",
            "converterRecipeKey",
            "coverageSha256",
            "seed",
        )
    }
    expected["corpusSha256"] = corpus_hash
    if manifest["coverageSha256"] != coverage_hash or any(
        receipt.get(key) != value for key, value in expected.items()
    ):
        raise ValueError(
            "Frozen corpus or generation provenance differs from the reviewed receipt"
        )


def read_raw(record, output):
    process = record.get("process", {})
    if process.get("timeout") is not False or process.get("returncode") != 0:
        return {}
    if record.get("failureReason") in ("missing_result", "evidence_error"):
        return {}
    path = (output / record["rawResult"]).resolve()
    if not path.is_relative_to(output.resolve()):
        raise ValueError("Raw evidence path escapes output directory")
    raw = json.loads(path.read_text(encoding="utf-8"))
    if raw.get("status") != record.get("status"):
        raise ValueError("Raw process result and audited status differ")
    if raw.get("status") in ("resource_limit", "invalid_input") and (
        raw.get("alternatives") != [] or not isinstance(raw.get("error"), dict)
    ):
        raise ValueError(
            "Resource/input refusal has inconsistent raw alternatives or error"
        )
    return raw


def route_metrics(raw):
    result = []
    profiles = set()
    for alternative in raw.get("alternatives", []):
        profile = alternative["name"]
        if not isinstance(profile, str) or not profile or profile in profiles:
            raise ValueError("Route profiles must be distinct nonempty names")
        profiles.add(profile)
        metrics = alternative["metrics"]
        values = [metrics.get(key) for key in ("LengthMetres", "WeightedCost")]
        if any(
            type(value) not in (int, float) or not math.isfinite(value) or value < 0
            for value in values
        ):
            raise ValueError("Route length and cost must be finite and nonnegative")
        result.append(
            {"profile": profile, "lengthMetres": values[0], "weightedCost": values[1]}
        )
    return result


def compact_result(case, record, output, witnesses=()):
    raw = read_raw(record, output)
    conclusion = classify(record, case["scenario"], witnesses)
    peak = raw.get("peakWorkingSetBytes")
    if peak is not None and (type(peak) is not int or peak <= 0):
        raise ValueError("Measured peak working set must be positive integer bytes")
    return {
        "id": case["id"],
        "split": case["group"],
        "region": case["region"],
        "categories": case["categories"],
        "distanceClass": case["distanceClass"],
        "complexity": case["complexity"],
        **conclusion,
        "causes": causes(
            record, case["scenario"], conclusion, categories=case["categories"]
        ),
        "wallMilliseconds": record.get("process", {}).get("elapsed_wall_ms"),
        "peakRssBytes": peak,
        "fallback": fallback_metrics(record),
        "alternatives": route_metrics(raw),
        "rawEvidence": Path(record["rawResult"]).as_posix(),
    }


def legacy_metadata(case):
    scenario = case["scenario"]
    kind = scenario.get("scenarioType", "road")
    categories = {"road"}
    if scenario.get("category"):
        categories.add("legacy-" + scenario["category"])
    if kind == "bridge" or case["id"] in ("footbridge", "indirect"):
        categories.add("bridge")
    if kind == "gas":
        categories.add("overhead-gas")
    if kind == "multi":
        categories.add("multi")
    for terrain in scenario.get("terrain", []):
        if terrain in ("Forest", "Field"):
            categories.add(terrain.lower())
    return case | {
        "region": LEGACY_REGIONS.get(scenario.get("area"), "BY-HM"),
        "categories": sorted(categories),
        "distanceClass": scenario.get("distanceClass", "legacy"),
        "complexity": "combined" if kind == "multi" else "legacy",
    }


def algorithm_provenance():
    files = {
        path.relative_to(ROOT).as_posix(): digest(path)
        for project in ("Domain", "Application", "Infrastructure", "Persistence")
        for path in sorted((ROOT / "src" / ("CableRoutePlanner." + project)).rglob("*"))
        if path.is_file()
        and (path.suffix in (".cs", ".csproj") or path.name == "packages.lock.json")
        if not {"bin", "obj"}.intersection(path.relative_to(ROOT).parts)
    }
    files.update(
        {
            name: digest(ROOT / name)
            for name in (
                "Directory.Build.props",
                "Directory.Packages.props",
                "global.json",
            )
        }
    )
    return {
        "algorithmSha256": hashlib.sha256(
            json.dumps(files, sort_keys=True).encode()
        ).hexdigest(),
        "algorithmFiles": files,
    }


def research_witnesses(case, record, output, data, timeout, known):
    """Use independent proposals only for development; holdout receives no research."""
    if record.get("status") != "no_route" or case["group"] == "holdout":
        return []
    folder = output / record["rawResult"]
    folder = folder.parent
    witnesses = []
    coverage = Polygon(json.loads((ROOT / "data/belarus-coverage.json").read_text()))
    if case["id"] in known:
        geometry = known[case["id"]]["geometry"]
        with closing(
            sqlite3.connect(
                (data / "geodata.sqlite").resolve().as_uri() + "?mode=ro", uri=True
            )
        ) as connection:
            checked = audit_alternative(
                connection,
                coverage,
                case["scenario"],
                {"geometry": geometry},
                enabled=record["effectiveUnderpasses"],
            )
        witnesses.append(
            {"kind": "known_recovery_witness", "geometry": geometry, "audit": checked}
        )
    if classify(record, case["scenario"], witnesses)["outcome"] != "indeterminate":
        save_json(folder / "independent-witnesses.json", witnesses)
        return witnesses
    destination = folder / "independent-research.json"
    destination.unlink(missing_ok=True)
    command = [
        sys.executable,
        str(ROOT / "tools/routing_research_witnesses.py"),
        "--data",
        str(data),
        "--scenario",
        str(folder / "original-scenario.json"),
        "--record",
        str(folder / "audit.json"),
        "--output",
        str(destination),
    ]
    process = run_bounded_command(command, timeout)
    save_json(folder / "independent-process.json", process)
    if process["timeout"]:
        record["independentResearch"] = {
            "status": "resource_limit",
            "reason": "probe_timeout",
        }
    elif process["returncode"] != 0 or not destination.exists():
        record["independentResearch"] = {
            "status": "technical_error",
            "reason": "probe_process_failed",
        }
    else:
        proposal = json.loads(destination.read_text(encoding="utf-8"))
        record["independentResearch"] = {
            key: proposal.get(key)
            for key in (
                "status",
                "reason",
                "elapsedSeconds",
                "features",
                "rawVertexCount",
                "componentCount",
                "connected",
            )
        }
        if proposal.get("status") == "witness":
            # Bind child evidence to exact invocation/options, then recheck in parent.
            if (
                proposal.get("originalScenario") != case["scenario"]
                or proposal.get("effectiveUnderpasses")
                is not record["effectiveUnderpasses"]
            ):
                raise ValueError("Independent witness input differs from invocation")
            geometry = proposal["alternative"]["geometry"]
            with closing(
                sqlite3.connect(
                    (data / "geodata.sqlite").resolve().as_uri() + "?mode=ro", uri=True
                )
            ) as connection:
                checked = audit_alternative(
                    connection,
                    coverage,
                    case["scenario"],
                    {"geometry": geometry},
                    enabled=record["effectiveUnderpasses"],
                )
            witnesses.append(
                {
                    "kind": "independent_constrained_mesh",
                    "geometry": geometry,
                    "audit": checked,
                }
            )
    save_json(folder / "independent-witnesses.json", witnesses)
    return witnesses


def write_report(report, records, output):
    report["summary"] = aggregate(records)
    public = [record for record in records if record["split"] != "holdout"]
    holdout = [record for record in records if record["split"] == "holdout"]
    report["splitMeasurements"] = {
        name: {
            key: value
            for key, value in aggregate(subset).items()
            if key in ("scenarioCount", "outcomes", "performance", "routes", "fallback")
        }
        for name, subset in (("nonHoldout", public), ("holdout", holdout))
    }
    if holdout:
        sealed = output / "sealed-holdout"
        sealed.mkdir(exist_ok=True)
        save_json(
            sealed / "results.json",
            {
                "policy": "Evaluation only; forbidden for development of subsequent fixes",
                "records": holdout,
            },
        )
        report["sealedEvaluationSha256"] = digest(sealed / "results.json")
    save_json(output / "development-records.json", {"records": public})
    report["developmentRecordsSha256"] = digest(output / "development-records.json")
    save_json(output / "provenance.json", report["provenance"])
    report["provenanceReceiptSha256"] = digest(output / "provenance.json")
    published = report | {
        "provenance": {
            key: value
            for key, value in report["provenance"].items()
            if key
            not in ("sourceFiles", "binaryFiles", "algorithmFiles", "evidenceHashes")
        }
    }
    save_json(output / "results.json", published)
    (output / "summary.md").write_text(markdown(report), encoding="utf-8")


def evidence_path(output, relative):
    path = (output / relative).resolve()
    if not path.is_relative_to(output.resolve()):
        raise ValueError("Evidence escapes experiment directory")
    return path


def validate_record_evidence(records, output, inventory):
    for record in records:
        path = evidence_path(output, record["rawEvidence"])
        for name in (
            "audit.json",
            "process.json",
            "scenario.json",
            "original-scenario.json",
        ):
            relative = (path.parent / name).relative_to(output.resolve()).as_posix()
            if relative not in inventory:
                raise ValueError("Required raw evidence is absent: " + relative)
        relative = path.relative_to(output.resolve()).as_posix()
        if relative not in inventory:
            process = json.loads(
                (path.parent / "process.json").read_text(encoding="utf-8")
            )
            if (
                record["outcome"] != "resource_limit"
                or process.get("timeout") is not True
            ):
                raise ValueError("Raw result is absent without timeout evidence")


def seal_evidence(records, output):
    """Bind every raw route, process, audit and witness to the completed report."""
    excluded = {
        "results.json",
        "summary.md",
        "provenance.json",
        "development-records.json",
        "evidence-manifest.json",
        "sealed-holdout/results.json",
        "sealed-holdout/evidence-manifest.json",
    }
    inventories = {"development": {}, "holdout": {}}
    for path in sorted(output.rglob("*")):
        if not path.is_file():
            continue
        relative = path.relative_to(output).as_posix()
        if relative in excluded:
            continue
        scope = (
            "holdout"
            if relative.startswith(("holdout/", "sealed-holdout/"))
            else "development"
        )
        inventories[scope][relative] = digest(path)
    all_files = inventories["development"] | inventories["holdout"]
    validate_record_evidence(records, output, all_files)
    save_json(output / "evidence-manifest.json", inventories["development"])
    receipts = {"rawEvidenceManifestSha256": digest(output / "evidence-manifest.json")}
    if any(record["split"] == "holdout" for record in records):
        sealed = output / "sealed-holdout"
        sealed.mkdir(exist_ok=True)
        save_json(sealed / "evidence-manifest.json", inventories["holdout"])
        receipts["sealedEvidenceManifestSha256"] = digest(
            sealed / "evidence-manifest.json"
        )
    return receipts


def load_baseline(path):
    report = json.loads(path.read_text(encoding="utf-8"))
    if (
        report.get("fatalError")
        or report.get("provenanceRevalidated", {}).get("unchanged") is not True
    ):
        raise ValueError("Baseline evidence is incomplete")
    records = []
    for filename, checksum in (
        ("development-records.json", report.get("developmentRecordsSha256")),
        ("sealed-holdout/results.json", report.get("sealedEvaluationSha256")),
    ):
        if checksum is None and filename.startswith("sealed-"):
            continue
        source = path.parent / filename
        if digest(source) != checksum:
            raise ValueError("Baseline record checksum differs: " + filename)
        records.extend(json.loads(source.read_text(encoding="utf-8"))["records"])
    if digest(path.parent / "provenance.json") != report.get("provenanceReceiptSha256"):
        raise ValueError("Baseline provenance checksum differs")
    if aggregate(records) != report.get("summary"):
        raise ValueError("Baseline aggregate differs from records")
    inventory = {}
    for filename, checksum in (
        ("evidence-manifest.json", report.get("rawEvidenceManifestSha256")),
        (
            "sealed-holdout/evidence-manifest.json",
            report.get("sealedEvidenceManifestSha256"),
        ),
    ):
        if checksum is None and filename.startswith("sealed-"):
            continue
        source = path.parent / filename
        if digest(source) != checksum:
            raise ValueError("Baseline raw evidence manifest differs")
        files = json.loads(source.read_text(encoding="utf-8"))
        for relative, expected in files.items():
            if digest(evidence_path(path.parent, relative)) != expected:
                raise ValueError("Baseline raw evidence changed: " + relative)
        inventory.update(files)
    validate_record_evidence(records, path.parent, inventory)
    return report | {"records": records}


def run(args):
    from routing_research_corpus import validate_manifest

    started = time.monotonic()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    report = {
        "schema": "maps.routing-research.v1",
        "recordedAt": datetime.datetime.now(datetime.UTC).isoformat(),
        "gatePassed": False,
        "provenance": {},
        "summary": aggregate([]),
    }
    records = []
    write_report(report, records, output)
    try:
        validate_budget(args.workers, args.timeout_seconds, args.probe_timeout_seconds)
        validate_build_location(args.verify_dll)
        manifest = json.loads(args.corpus.read_text(encoding="utf-8"))
        validate_manifest(manifest, legacy_sha256=digest(CORPUS))
        receipt = json.loads(args.generation_receipt.read_text(encoding="utf-8"))
        validate_generation_receipt(
            manifest,
            digest(args.corpus),
            receipt,
            digest(ROOT / "data/belarus-coverage.json"),
        )
        data = data_provenance(args.data)
        if (
            data["sourceSha256"] != manifest["sourceSha256"]
            or digest(CORPUS) != CORPUS_SHA256
        ):
            raise ValueError(
                "Research source or legacy corpus differs from frozen inputs"
            )
        # Byte-identical SQLite is required within a comparison. Cross-platform builds
        # of the same pinned converter/PBF have different SQLite page layouts.
        lock = json.loads(
            (ROOT / "tools/GeodataPreparation/belarus.lock.json").read_text()
        )
        from GeodataPreparation.country_data import cache_key

        if data.get("packageKey") != cache_key(lock, ROOT) or manifest.get(
            "converterRecipeKey"
        ) != data.get("packageKey"):
            raise ValueError(
                "Prepared country recipe differs from current pinned converter"
            )
        cases = [legacy_metadata(case) for case in load_cases(ROOT, CORPUS)]
        cases += [
            {
                "id": item["id"],
                "group": item["split"],
                "scenario": item["scenario"],
                "expected": "unknown",
                "sourceFile": str(args.corpus),
                "sourceFileSha256": digest(args.corpus),
                **{
                    key: item[key]
                    for key in ("region", "categories", "distanceClass", "complexity")
                },
            }
            for item in selected_cases(manifest["cases"], args.evaluate_holdout)
        ]
        expected = {case["id"] for case in cases if case["group"] != "control"}
        if len({case["id"] for case in cases}) != len(cases):
            raise ValueError("Corpus and control identifiers overlap")
        code = code_provenance(ROOT, args.verify_dll, CORPUS)
        report["provenance"] = (
            code
            | data
            | algorithm_provenance()
            | {
                "corpusSha256": digest(args.corpus),
                "legacyCorpusSha256": digest(CORPUS),
                "workers": args.workers,
                "timeoutSeconds": args.timeout_seconds,
                "probeTimeoutSeconds": args.probe_timeout_seconds,
                "managedHeapBytes": MANAGED_HEAP_BYTES,
                "evaluateHoldout": args.evaluate_holdout,
                "logicalCpuCount": os.cpu_count(),
                "processor": platform.processor() or platform.machine(),
                "assessmentSha256": hashlib.sha256(
                    json.dumps(
                        {
                            name: digest(ROOT / name)
                            for name in (
                                "tools/routing_crossing_pilot.py",
                                "tools/routing_regression.py",
                                "tools/routing_recovery.py",
                                "tools/routing_portal_witnesses.py",
                                "tools/routing_research_analysis.py",
                                "tools/routing_research_witnesses.py",
                                "docs/evidence/routing-recovery/independent_probe.py",
                                "src/CableRoutePlanner.Domain/TerrainTraversalPolicy.cs",
                                "src/CableRoutePlanner.Infrastructure/GeodataPointPlacementValidator.cs",
                            )
                        },
                        sort_keys=True,
                    ).encode()
                ).hexdigest(),
                "effectiveOptionsSha256": hashlib.sha256(
                    json.dumps(
                        {
                            case["id"]: case["scenario"]["routingOptions"]
                            | {
                                "AllowExperimentalPedestrianUnderpasses": case[
                                    "scenario"
                                ]["routingOptions"].get(
                                    "AllowExperimentalPedestrianUnderpasses",
                                    code["effectiveDefaultUnderpasses"],
                                )
                            }
                            for case in cases
                        },
                        sort_keys=True,
                    ).encode()
                ).hexdigest(),
            }
        )
        paths = {
            "corpus": args.corpus,
            "generationReceipt": args.generation_receipt,
            "database": args.data / "geodata.sqlite",
            "manifest": args.data / "manifest.json",
            "receipt": args.data / "package.json",
        }
        paths.update({"source/" + name: ROOT / name for name in code["sourceFiles"]})
        paths.update(
            {
                "source/" + name: ROOT / name
                for name in report["provenance"]["algorithmFiles"]
            }
        )
        paths.update(
            {
                "binary/" + name: args.verify_dll.parent / name
                for name in code["binaryFiles"]
            }
        )
        paths.update(
            {
                "research/" + path.name: path
                for path in (ROOT / "tools").glob("routing_research*.py")
            }
        )
        before = evidence_snapshot(paths)
        report["provenance"]["evidenceHashes"] = before
        shutil.copyfile(args.corpus, output / "frozen-research-corpus.json")
        shutil.copyfile(args.generation_receipt, output / "generation-receipt.json")
        regression_output = output / "regression"
        regression_args = SimpleNamespace(
            data=args.data,
            verify_dll=args.verify_dll,
            corpus=CORPUS,
            output=regression_output,
            workers=args.workers,
            timeout_seconds=args.timeout_seconds,
            baseline_verify_dll=None,
            baseline_source=None,
        )
        frozen_exit = run_regression(regression_args)
        regression = json.loads(
            (regression_output / "results.json").read_text(encoding="utf-8")
        )
        report["frozenRegressionGatePassed"] = frozen_exit == 0
        from routing_portal_witnesses import compare_run_report
        from routing_portal_witnesses import investigate as inspect_portals
        from routing_recovery import investigate as inspect_recovery

        recovery = inspect_recovery(
            args.data,
            CORPUS,
            ROOT / CATALOGUE,
            ROOT / "data/belarus-coverage.json",
            regression_output / "results.json",
        )
        save_json(output / "known-recovery-audit.json", recovery)
        portals = inspect_portals(
            args.data / "geodata.sqlite", CORPUS, ROOT / "data/belarus-coverage.json"
        )
        portals["comparison"] = compare_run_report(portals, regression)
        save_json(output / "portal-witness-audit.json", portals)
        report["independentLegacyGates"] = {
            "knownRecovery": recovery["gatePassed"],
            "portalWitnesses": portals["comparison"]["gatePassed"],
        }
        report["controls"] = [
            {
                key: record.get(key)
                for key in ("id", "status", "gatePassed", "failureReason")
            }
            for record in regression["results"]
            if record["group"] == "control"
        ]
        known = {
            record["id"]: record for record in load_catalogue(ROOT, CORPUS)["records"]
        }
        by_id = {case["id"]: case for case in cases}
        for record in regression["results"]:
            if record["group"] != "control":
                case = by_id[record["id"]]
                witnesses = research_witnesses(
                    case,
                    record,
                    regression_output,
                    args.data,
                    args.probe_timeout_seconds,
                    known,
                )
                compact = compact_result(case, record, regression_output, witnesses)
                compact["rawEvidence"] = "regression/" + compact["rawEvidence"]
                compact["independentResearch"] = record.get("independentResearch")
                records.append(compact)
                write_report(report, records, output)
        coverage = Polygon(
            json.loads((ROOT / "data/belarus-coverage.json").read_text())
        )
        new_cases = [
            case for case in cases if case["group"] in ("development", "holdout")
        ]
        for record in run_cases(
            new_cases,
            args.data,
            args.verify_dll,
            output,
            coverage,
            default_underpasses=code["effectiveDefaultUnderpasses"],
            timeout_seconds=args.timeout_seconds,
            workers=args.workers,
        ):
            case = by_id[record["id"]]
            try:
                if record.get("status") == "invalid_input":
                    from routing_research_witnesses import point_refusal_proof

                    with closing(
                        sqlite3.connect(
                            (args.data / "geodata.sqlite").resolve().as_uri()
                            + "?mode=ro",
                            uri=True,
                        )
                    ) as connection:
                        record["inputRefusalProof"] = point_refusal_proof(
                            connection,
                            coverage,
                            case["scenario"],
                            record.get("error") or {},
                        )
                witnesses = research_witnesses(
                    case, record, output, args.data, args.probe_timeout_seconds, known
                )
                compact = compact_result(case, record, output, witnesses)
            except (OSError, ValueError, KeyError, TypeError, sqlite3.Error) as error:
                record.update(
                    failureReason="evidence_error",
                    evidenceError={"type": type(error).__name__, "detail": str(error)},
                )
                save_json(
                    output
                    / record["rawResult"].replace("result.json", "research-error.json"),
                    record,
                )
                compact = compact_result(case, record, output)
            compact["independentResearch"] = record.get("independentResearch")
            save_json(
                (output / record["rawResult"]).parent / "research-audit.json", record
            )
            records.append(compact)
            write_report(report, records, output)
            print(
                f"Research progress: {len(records)}/{len(expected)}"
                + (
                    f"; {case['id']}: {compact['outcome']}"
                    if case["group"] != "holdout"
                    else "; sealed evaluation"
                ),
                flush=True,
            )
        verify_inventory(expected, records)
        report.update(seal_evidence(records, output))
        validate_snapshot(paths, before)
        report["provenanceRevalidated"] = {
            "unchanged": True,
            "checkedFiles": len(paths),
        }
        report.update(
            experiment_gates(
                frozen_exit == 0 and all(report["independentLegacyGates"].values()),
                records,
            )
        )
        report["independentResearchCounts"] = dict(
            Counter(
                (record.get("independentResearch") or {}).get(
                    "status", "not_needed_or_sealed"
                )
                for record in records
            )
        )
        if args.baseline_report:
            baseline = load_baseline(args.baseline_report)
            head = report | {"records": records}
            report["comparison"] = compare(head, baseline)
            report["gatePassed"] &= report["comparison"]["lostValidRoutes"] == 0
    except (
        OSError,
        ValueError,
        KeyError,
        TypeError,
        sqlite3.Error,
        subprocess.CalledProcessError,
    ) as error:
        report["fatalError"] = {"type": type(error).__name__, "detail": str(error)}
        report["gatePassed"] = False
    report["elapsedSeconds"] = round(time.monotonic() - started, 3)
    write_report(report, records, output)
    return 0 if report["gatePassed"] else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--verify-dll", type=Path, required=True)
    parser.add_argument("--corpus", type=Path, default=CORPUS_PATH)
    parser.add_argument("--generation-receipt", type=Path, default=GENERATION_RECEIPT)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--baseline-report", type=Path)
    parser.add_argument("--workers", type=int, default=2)
    parser.add_argument("--timeout-seconds", type=float, default=120)
    parser.add_argument("--probe-timeout-seconds", type=float, default=30)
    parser.add_argument(
        "--evaluate-holdout",
        action="store_true",
        help="Explicit evaluation only; never use individual held-out failures for development",
    )
    args = parser.parse_args()
    return run(args)


if __name__ == "__main__":
    raise SystemExit(main())
