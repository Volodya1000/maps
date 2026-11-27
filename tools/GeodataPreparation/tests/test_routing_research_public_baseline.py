"""Public comparisons validate full-run public evidence without opening holdout."""

import copy
import hashlib
import importlib
import json
import shutil
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
runner = importlib.import_module("routing_research")
analysis = importlib.import_module("routing_research_analysis")


def options_digest(cases, default_underpasses, *, public_only=False):
    payload = {
        case["id"]: case["scenario"]["routingOptions"]
        | {
            "AllowExperimentalPedestrianUnderpasses": case["scenario"][
                "routingOptions"
            ].get("AllowExperimentalPedestrianUnderpasses", default_underpasses)
        }
        for case in cases
        if not public_only or case["group"] != "holdout"
    }
    return hashlib.sha256(json.dumps(payload, sort_keys=True).encode()).hexdigest()


class ResearchPublicBaselineTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.full = self.root / "full"
        self.full.mkdir()
        self.cases = [
            {
                "id": "dev",
                "group": "development",
                "scenario": {"routingOptions": {"CellSizeMetres": 25}},
            },
            {
                "id": "synthetic-secret",
                "group": "holdout",
                "scenario": {"routingOptions": {"CellSizeMetres": 50}},
            },
        ]
        self.records = []
        for case in self.cases:
            record = {
                "id": case["id"],
                "split": case["group"],
                "region": "BY-HM",
                "categories": ["road-local"],
                "distanceClass": "local",
                "complexity": "single",
                "outcome": "valid_route",
                "causes": [],
                "alternatives": [
                    {"profile": "short", "lengthMetres": 100, "weightedCost": 200}
                ],
                "peakRssBytes": 10000,
                "wallMilliseconds": 42,
                "fallback": None,
                "rawEvidence": f"{case['group']}/{case['id']}/result.json",
            }
            self.records.append(record)
            folder = (self.full / record["rawEvidence"]).parent
            folder.mkdir(parents=True)
            raw = {
                "result.json": {
                    "status": "success",
                    "alternatives": [
                        {
                            "name": "short",
                            "metrics": {"LengthMetres": 100, "WeightedCost": 200},
                        }
                    ],
                },
                "audit.json": {
                    "id": case["id"],
                    "status": "success",
                    "audits": [{"status": "valid", "findings": []}],
                },
                "process.json": {
                    "timeout": False,
                    "returncode": 0,
                    "elapsed_wall_ms": 42,
                },
                "scenario.json": case["scenario"],
                "original-scenario.json": case["scenario"],
            }
            for name, payload in raw.items():
                runner.save_json(folder / name, payload)

        def sha(value):
            return hashlib.sha256(value.encode()).hexdigest()

        self.provenance = {
            "corpusSha256": sha("corpus"),
            "databaseSha256": sha("database"),
            "assessmentSha256": sha("assessment"),
            "algorithmSha256": sha("algorithm"),
            "workers": 2,
            "timeoutSeconds": 120,
            "managedHeapBytes": 1536 * 1024 * 1024,
            "probeTimeoutSeconds": 30,
            "evaluateHoldout": True,
            "effectiveOptionsSha256": options_digest(self.cases, True),
            "nonHoldoutEffectiveOptionsSha256": options_digest(
                self.cases, True, public_only=True
            ),
            "sourceFiles": {"source.py": sha("source")},
            "platform": "synthetic",
            "processor": "synthetic",
            "logicalCpuCount": 2,
        }
        report = {
            "provenance": copy.deepcopy(self.provenance),
            "provenanceRevalidated": {"unchanged": True},
            "gatePassed": True,
        }
        report.update(runner.seal_evidence(self.records, self.full))
        runner.write_report(report, self.records, self.full)
        self.public = self.root / "primary"
        shutil.copytree(
            self.full,
            self.public,
            ignore=shutil.ignore_patterns("holdout", "sealed-holdout"),
        )

    def no_sealed_reads(self):
        original = Path.open

        def guarded(path, *args, **kwargs):
            if {"holdout", "sealed-holdout"}.intersection(
                part.casefold() for part in path.parts
            ):
                raise AssertionError("Public projection tried to open sealed evidence")
            return original(path, *args, **kwargs)

        return patch.object(Path, "open", guarded)

    def published(self):
        return json.loads((self.public / "results.json").read_text(encoding="utf-8"))

    def save_published(self, value):
        runner.save_json(self.public / "results.json", value)

    def head(self):
        provenance = copy.deepcopy(self.provenance)
        provenance.update(
            evaluateHoldout=False,
            effectiveOptionsSha256=provenance["nonHoldoutEffectiveOptionsSha256"],
        )
        records = copy.deepcopy(self.records[:1])
        return {
            "provenance": provenance,
            "records": records,
            "summary": analysis.aggregate(records),
        }

    def test_full_fixture_is_valid_and_full_mode_requires_sealed_evidence(self):
        self.assertEqual(
            2, len(runner.load_baseline(self.full / "results.json")["records"])
        )
        self.assertFalse((self.public / "sealed-holdout").exists())
        with self.assertRaises((ValueError, FileNotFoundError)):
            runner.load_baseline(self.public / "results.json")

    def test_public_projection_compares_without_any_sealed_files_or_reads(self):
        with self.no_sealed_reads():
            projected = runner.load_baseline(
                self.public / "results.json", public_only=True
            )
            comparison = analysis.compare(self.head(), projected)
        self.assertEqual(0, comparison["lostValidRoutes"])
        self.assertEqual(self.records[:1], projected["records"])
        self.assertEqual(analysis.aggregate(self.records[:1]), projected["summary"])
        self.assertEqual("nonHoldout", projected["baselineProjection"]["scope"])
        self.assertFalse(projected["provenance"]["evaluateHoldout"])
        self.assertEqual(
            self.provenance["nonHoldoutEffectiveOptionsSha256"],
            projected["provenance"]["effectiveOptionsSha256"],
        )
        self.assertEqual(
            self.provenance["sourceFiles"], projected["provenance"]["sourceFiles"]
        )
        self.assertTrue(self.published()["provenance"]["evaluateHoldout"])

    def test_public_valid_route_loss_is_counted_after_projection(self):
        with self.no_sealed_reads():
            projected = runner.load_baseline(
                self.public / "results.json", public_only=True
            )
        head = self.head()
        head["records"][0]["outcome"] = "indeterminate"
        head["summary"] = analysis.aggregate(head["records"])
        self.assertEqual(1, analysis.compare(head, projected)["lostValidRoutes"])

    def test_full_to_public_projection_requires_public_options_digest(self):
        report = self.published()
        receipt = json.loads((self.public / "provenance.json").read_text())
        del report["provenance"]["nonHoldoutEffectiveOptionsSha256"]
        del receipt["nonHoldoutEffectiveOptionsSha256"]
        runner.save_json(self.public / "provenance.json", receipt)
        report["provenanceReceiptSha256"] = runner.digest(
            self.public / "provenance.json"
        )
        self.save_published(report)
        with self.no_sealed_reads(), self.assertRaises(ValueError):
            runner.load_baseline(self.public / "results.json", public_only=True)

    def test_public_raw_tampering_is_rejected(self):
        runner.save_json(
            self.public / "development/dev/result.json", {"tampered": True}
        )
        with self.no_sealed_reads(), self.assertRaises(ValueError):
            runner.load_baseline(self.public / "results.json", public_only=True)

    def test_holdout_record_in_public_records_is_rejected_before_open(self):
        runner.save_json(
            self.public / "development-records.json", {"records": self.records}
        )
        report = self.published()
        report["developmentRecordsSha256"] = runner.digest(
            self.public / "development-records.json"
        )
        self.save_published(report)
        with self.no_sealed_reads(), self.assertRaises(ValueError):
            runner.load_baseline(self.public / "results.json", public_only=True)

    def test_public_record_pointing_to_holdout_evidence_is_rejected_before_open(self):
        for relative in (
            "holdout/synthetic-secret/result.json",
            "sealed-holdout/results.json",
            "HOLDOUT/synthetic-secret/result.json",
            "SEALED-HOLDOUT/results.json",
            "development/../holdout/synthetic-secret/result.json",
        ):
            with self.subTest(relative=relative):
                records = copy.deepcopy(self.records[:1])
                records[0]["rawEvidence"] = relative
                runner.save_json(
                    self.public / "development-records.json", {"records": records}
                )
                report = self.published()
                report["developmentRecordsSha256"] = runner.digest(
                    self.public / "development-records.json"
                )
                self.save_published(report)
                with self.no_sealed_reads(), self.assertRaises(ValueError):
                    runner.load_baseline(self.public / "results.json", public_only=True)

    def test_public_manifest_holdout_paths_are_rejected_before_open(self):
        original = json.loads((self.public / "evidence-manifest.json").read_text())
        for relative in (
            "holdout/synthetic-secret/result.json",
            "sealed-holdout/forbidden.json",
            "HOLDOUT/synthetic-secret/result.json",
            "SEALED-HOLDOUT/forbidden.json",
            "development/../sealed-holdout/forbidden.json",
        ):
            with self.subTest(relative=relative):
                inventory = dict(original, **{relative: "0" * 64})
                runner.save_json(self.public / "evidence-manifest.json", inventory)
                report = self.published()
                report["rawEvidenceManifestSha256"] = runner.digest(
                    self.public / "evidence-manifest.json"
                )
                self.save_published(report)
                with self.no_sealed_reads(), self.assertRaises(ValueError):
                    runner.load_baseline(self.public / "results.json", public_only=True)

    def test_every_public_measurement_is_validated_against_public_records(self):
        original = self.published()
        for field, nested in (
            ("scenarioCount", None),
            ("outcomes", "valid_route"),
            ("performance", "sumProcessWallMilliseconds"),
            ("routes", "alternativeCount"),
            ("fallback", "measuredCases"),
        ):
            with self.subTest(field=field):
                report = copy.deepcopy(original)
                measurement = report["splitMeasurements"]["nonHoldout"]
                if nested is None:
                    measurement[field] += 1
                else:
                    measurement[field][nested] += 1
                self.save_published(report)
                with self.no_sealed_reads(), self.assertRaises(ValueError):
                    runner.load_baseline(self.public / "results.json", public_only=True)

    def test_inline_provenance_contradicting_hashed_receipt_is_rejected(self):
        report = self.published()
        report["provenance"]["databaseSha256"] = "f" * 64
        self.save_published(report)
        with self.no_sealed_reads(), self.assertRaises(ValueError):
            runner.load_baseline(self.public / "results.json", public_only=True)

    def test_public_options_digest_includes_all_controls_and_excludes_only_holdout(
        self,
    ):
        cases = copy.deepcopy(self.cases)
        cases.extend(
            {
                "id": f"control-{index}",
                "group": "control",
                "scenario": {
                    "routingOptions": {
                        "CellSizeMetres": 25,
                        "AllowExperimentalPedestrianUnderpasses": False,
                    }
                },
            }
            for index in range(6)
        )
        actual = runner.effective_options_sha256(cases, True, public_only=True)
        self.assertEqual(options_digest(cases, True, public_only=True), actual)
        self.assertNotEqual(
            actual, runner.effective_options_sha256(cases, False, public_only=True)
        )
        self.assertEqual(
            options_digest(cases, False, public_only=True),
            runner.effective_options_sha256(cases, False, public_only=True),
        )
        self.assertEqual(
            options_digest(cases, True), runner.effective_options_sha256(cases, True)
        )
        changed_holdout = copy.deepcopy(cases)
        changed_holdout[1]["scenario"]["routingOptions"]["CellSizeMetres"] = 75
        self.assertEqual(
            actual,
            runner.effective_options_sha256(changed_holdout, True, public_only=True),
        )
        self.assertNotEqual(
            runner.effective_options_sha256(cases, True),
            runner.effective_options_sha256(changed_holdout, True),
        )
        for index in range(2, 8):
            with self.subTest(control=cases[index]["id"]):
                changed = copy.deepcopy(cases)
                changed[index]["scenario"]["routingOptions"]["MaxNodes"] = 20
                self.assertNotEqual(
                    actual,
                    runner.effective_options_sha256(changed, True, public_only=True),
                )
