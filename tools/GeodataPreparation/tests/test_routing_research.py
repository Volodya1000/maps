"""Corpus execution must preserve identity, budgets and complete evidence."""

import importlib
import json
import sqlite3
import sys
import tempfile
import unittest
from contextlib import closing
from pathlib import Path

from shapely.geometry import Polygon

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))


class ResearchRunnerTests(unittest.TestCase):
    def setUp(self):
        self.runner = importlib.import_module("routing_research")

    def test_inventory_rejects_missing_duplicate_or_extra_results(self):
        expected = {"first", "second"}
        for results in (
            [{"id": "first"}],
            [{"id": "first"}] * 2,
            [{"id": "first"}, {"id": "second"}, {"id": "third"}],
        ):
            with self.subTest(results=results), self.assertRaises(ValueError):
                self.runner.verify_inventory(expected, results)
        self.runner.verify_inventory(expected, [{"id": "second"}, {"id": "first"}])

    def test_first_experiment_records_discoveries_without_weakening_frozen_gate(self):
        findings = [{"outcome": "confirmed_missed_route"}, {"outcome": "invalid_route"}]
        gates = self.runner.experiment_gates(True, findings)
        self.assertTrue(gates["gatePassed"])
        self.assertFalse(gates["qualityGatePassed"])
        self.assertFalse(self.runner.experiment_gates(False, findings)["gatePassed"])
        self.assertFalse(
            self.runner.experiment_gates(True, [{"outcome": "technical_error"}])[
                "gatePassed"
            ]
        )

    def test_execution_limits_reject_nonfinite_timeout_and_excessive_workers(self):
        for workers, timeout in (
            (0, 120),
            (9, 120),
            (2, float("nan")),
            (2, 0),
            (2, -1),
        ):
            with (
                self.subTest(workers=workers, timeout=timeout),
                self.assertRaises(ValueError),
            ):
                self.runner.validate_budget(workers, timeout, 30)
        self.runner.validate_budget(2, 120, 30)

    def test_foreign_binary_cannot_be_labelled_as_current_algorithm_source(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            build = (
                root
                / "tools/CableRoutePlanner.Verify/bin/Release/net8.0/CableRoutePlanner.Verify.dll"
            )
            build.parent.mkdir(parents=True)
            build.write_bytes(b"current build")
            self.runner.validate_build_location(build, root)
            foreign = root / "old-version.dll"
            foreign.write_bytes(b"older build")
            with self.assertRaises(ValueError):
                self.runner.validate_build_location(foreign, root)

    def test_frozen_generation_receipt_rejects_semantically_valid_input_changes(self):
        manifest = {
            "sourceSha256": "source",
            "legacyCorpusSha256": "legacy",
            "converterRecipeKey": "recipe",
            "coverageSha256": "coverage",
            "seed": 20261127,
        }
        receipt = manifest | {"corpusSha256": "frozen"}
        self.runner.validate_generation_receipt(manifest, "frozen", receipt, "coverage")
        for field in receipt:
            wrong = receipt | {field: "changed"}
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.runner.validate_generation_receipt(
                    manifest, "frozen", wrong, "coverage"
                )

    def test_compaction_preserves_route_metrics_memory_and_measured_fallback(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary)
            (output / "result.json").write_text(
                json.dumps(
                    {
                        "status": "success",
                        "peakWorkingSetBytes": 123456,
                        "alternatives": [
                            {
                                "name": "p",
                                "metrics": {"LengthMetres": 123, "WeightedCost": 456},
                            }
                        ],
                    }
                ),
                encoding="utf-8",
            )
            record = {
                "id": "case",
                "status": "success",
                "process": {"timeout": False, "returncode": 0, "elapsed_wall_ms": 42},
                "rawResult": "result.json",
                "audits": [{"status": "valid"}],
                "diagnostics": {
                    "droppedEvents": 0,
                    "events": [
                        {"eventId": 5, "fields": {}},
                        {"eventId": 9, "fields": {"Found": True}},
                    ],
                },
            }
            case = {
                "id": "case",
                "group": "development",
                "region": "BY-HM",
                "categories": ["road"],
                "distanceClass": "local",
                "complexity": "single",
                "scenario": {"points": [], "routingOptions": {}},
            }
            result = self.runner.compact_result(case, record, output)
            self.assertEqual(
                ("valid_route", 123456, 42),
                (result["outcome"], result["peakRssBytes"], result["wallMilliseconds"]),
            )
            self.assertEqual(
                [{"profile": "p", "lengthMetres": 123, "weightedCost": 456}],
                result["alternatives"],
            )
            self.assertEqual(1, result["fallback"]["surfaceFound"])

    def test_raw_path_escape_and_raw_status_mismatch_are_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary)
            record = {
                "process": {"timeout": False, "returncode": 0},
                "rawResult": "../outside.json",
                "status": "success",
            }
            with self.assertRaises(ValueError):
                self.runner.read_raw(record, output)
            record["rawResult"] = "result.json"
            (output / "result.json").write_text(
                '{"status":"no_route"}', encoding="utf-8"
            )
            with self.assertRaises(ValueError):
                self.runner.read_raw(record, output)

    def test_timeout_does_not_reuse_stale_raw_result_or_invent_memory(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary)
            (output / "result.json").write_text(
                '{"status":"success","peakWorkingSetBytes":42}', encoding="utf-8"
            )
            record = {
                "id": "case",
                "status": "error",
                "process": {"timeout": True, "returncode": None},
                "rawResult": "result.json",
            }
            case = {
                "id": "case",
                "group": "development",
                "region": "BY-HM",
                "categories": ["road"],
                "distanceClass": "local",
                "complexity": "single",
                "scenario": {"points": [], "routingOptions": {}},
            }
            result = self.runner.compact_result(case, record, output)
            self.assertEqual("resource_limit", result["outcome"])
            self.assertIsNone(result["peakRssBytes"])
            self.assertEqual([], result["alternatives"])

    def test_nonfinite_negative_or_missing_route_metrics_fail_closed(self):
        for metrics in (
            {},
            {"LengthMetres": -1, "WeightedCost": 1},
            {"LengthMetres": 1, "WeightedCost": float("inf")},
        ):
            with self.subTest(metrics=metrics), self.assertRaises(ValueError):
                self.runner.route_metrics(
                    {"alternatives": [{"name": "p", "metrics": metrics}]}
                )

    def test_holdout_is_excluded_unless_explicitly_requested(self):
        cases = [
            {"split": "development", "id": "dev"},
            {"split": "holdout", "id": "secret"},
        ]
        self.assertEqual(
            ["dev"], [case["id"] for case in self.runner.selected_cases(cases, False)]
        )
        self.assertEqual(2, len(self.runner.selected_cases(cases, True)))

    def test_legacy_metadata_preserves_original_category_without_inventing_water(self):
        scenario = {
            "area": "naliboki",
            "category": "A",
            "scenarioType": "bridge",
            "terrain": ["Forest"],
        }
        case = {"id": "legacy", "scenario": scenario}
        result = self.runner.legacy_metadata(case)
        self.assertEqual(scenario, result["scenario"])
        self.assertEqual("BY-MI", result["region"])
        self.assertTrue({"legacy-A", "bridge", "forest"}.issubset(result["categories"]))
        self.assertNotIn("water", result["categories"])
        self.assertNotIn("bridge_water", result["categories"])

    def test_aggregate_json_separates_details_and_baseline_rejects_tampering(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary)
            record = {
                "id": "dev",
                "split": "development",
                "region": "BY-HM",
                "categories": ["road"],
                "distanceClass": "local",
                "complexity": "single",
                "outcome": "indeterminate",
                "causes": [],
                "alternatives": [],
                "peakRssBytes": None,
                "wallMilliseconds": 10,
                "fallback": None,
                "rawEvidence": "development/dev/result.json",
            }
            report = {
                "provenance": {
                    "sourceFiles": {"source": "hash"},
                    "corpusSha256": "frozen",
                },
                "provenanceRevalidated": {"unchanged": True},
                "gatePassed": True,
            }
            folder = output / "development/dev"
            folder.mkdir(parents=True)
            for name in (
                "result.json",
                "audit.json",
                "process.json",
                "scenario.json",
                "original-scenario.json",
            ):
                (folder / name).write_text("{}", encoding="utf-8")
            report.update(self.runner.seal_evidence([record], output))
            self.runner.write_report(report, [record], output)
            published = json.loads((output / "results.json").read_text())
            self.assertNotIn("records", published)
            self.assertNotIn("sourceFiles", published["provenance"])
            self.assertEqual(
                [record], self.runner.load_baseline(output / "results.json")["records"]
            )
            (folder / "result.json").write_text('{"tampered":true}', encoding="utf-8")
            with self.assertRaises(ValueError):
                self.runner.load_baseline(output / "results.json")
            (folder / "result.json").write_text("{}", encoding="utf-8")
            (output / "development-records.json").write_text(
                '{"records":[]}', encoding="utf-8"
            )
            with self.assertRaises(ValueError):
                self.runner.load_baseline(output / "results.json")

    def test_baseline_missing_raw_evidence_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary)
            record = {
                "id": "one",
                "split": "development",
                "rawEvidence": "development/one/result.json",
                "outcome": "valid_route",
            }
            with self.assertRaises(ValueError):
                self.runner.seal_evidence([record], output)

    def test_independent_point_proof_uses_actual_blocked_geometry(self):
        witnesses = importlib.import_module("routing_research_witnesses")
        with closing(sqlite3.connect(":memory:")) as connection:
            connection.executescript(
                "CREATE TABLE features(id,kind,wkb,source_id,approval_id); CREATE TABLE feature_index(id,minx,maxx,miny,maxy);"
            )
            building = Polygon(
                [(26.99, 53.99), (27.001, 53.99), (27.001, 54.001), (26.99, 54.001)]
            )
            connection.execute(
                "INSERT INTO features VALUES(1,'Building',?,'way/1',NULL)",
                (building.wkb,),
            )
            west, south, east, north = building.bounds
            connection.execute(
                "INSERT INTO feature_index VALUES(1,?,?,?,?)",
                (west, east, south, north),
            )
            scenario = {
                "points": [
                    {"Longitude": 27, "Latitude": 54},
                    {"Longitude": 27.01, "Latitude": 54},
                ]
            }
            coverage = Polygon([(26, 53), (28, 53), (28, 55), (26, 55)])
            proof = witnesses.point_refusal_proof(
                connection,
                coverage,
                scenario,
                {"reason": "BlockedTerrain", "terrainKind": "Building"},
            )
            self.assertEqual(
                (0, "way/1", "Building"),
                (proof["pointIndex"], proof["sourceId"], proof["terrainKind"]),
            )
            self.assertIsNone(
                witnesses.point_refusal_proof(
                    connection,
                    coverage,
                    scenario,
                    {"reason": "BlockedTerrain", "terrainKind": "Marsh"},
                )
            )
